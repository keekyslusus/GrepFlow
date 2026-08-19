using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrepFlow.Interop;

internal sealed record SublimeTextSessionFolder(string Path, string? Name)
{
    public string Label => string.IsNullOrWhiteSpace(Name)
        ? System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
        : Name.Trim();
}

internal sealed record SublimeTextSessionWindow(
    long WindowId,
    SublimeTextWindowBounds? Bounds,
    string? Project,
    string? WorkspaceName,
    IReadOnlyList<SublimeTextSessionFolder> Folders);

internal sealed record SublimeTextSession(IReadOnlyList<SublimeTextSessionWindow> Windows);

public sealed class SublimeTextSessionReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _sessionPath;
    private readonly PluginLog? _log;
    private string? _lastWarnFingerprint;

    public SublimeTextSessionReader(PluginLog? log = null)
        : this(DefaultSessionPath(), log)
    {
    }

    public SublimeTextSessionReader(string sessionPath, PluginLog? log = null)
    {
        _sessionPath = sessionPath;
        _log = log;
    }

    public static string DefaultSessionPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Sublime Text",
            "Local",
            "Auto Save Session.sublime_session");

    internal SublimeTextSession? TryReadSession()
    {
        if (!File.Exists(_sessionPath)) return null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    _sessionPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var dto = JsonSerializer.Deserialize<SessionDto>(stream, JsonOptions);
                return Convert(dto);
            }
            catch (IOException exception)
            {
                if (attempt == 2) WarnOnce("io", exception.Message);
            }
            catch (JsonException exception)
            {
                if (attempt == 2) WarnOnce("json", exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                WarnOnce("access", exception.Message);
                return null;
            }

            if (attempt < 2) Thread.Sleep(10);
        }

        return null;
    }

    internal static bool TryParsePosition(string? value, out SublimeTextWindowBounds? bounds)
    {
        bounds = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 5) return false;

        var offset = parts.Length - 5;
        if (!int.TryParse(parts[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bottom) ||
            !int.TryParse(parts[offset + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) ||
            !int.TryParse(parts[offset + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) ||
            !int.TryParse(parts[offset + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var right) ||
            right <= left ||
            bottom <= top ||
            !IsPlausible(left) ||
            !IsPlausible(top) ||
            !IsPlausible(right) ||
            !IsPlausible(bottom))
            return false;

        bounds = new SublimeTextWindowBounds(left, top, right, bottom);
        return true;
    }

    private static SublimeTextSession Convert(SessionDto? dto)
    {
        var windows = new List<SublimeTextSessionWindow>();
        if (dto?.Windows is null) return new SublimeTextSession(windows);

        foreach (var window in dto.Windows)
        {
            if (window.WindowId is null) continue;

            TryParsePosition(window.Position, out var bounds);
            var folders = NormalizeFolders(window.Folders, window.Project);
            windows.Add(new SublimeTextSessionWindow(
                window.WindowId.Value,
                bounds,
                window.Project,
                window.WorkspaceName,
                folders));
        }

        return new SublimeTextSession(windows);
    }

    private static IReadOnlyList<SublimeTextSessionFolder> NormalizeFolders(
        IReadOnlyList<FolderDto>? folders,
        string? project)
    {
        var result = new List<SublimeTextSessionFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folders is null) return result;

        var projectDirectory = ProjectDirectory(project);
        foreach (var folder in folders)
        {
            var path = NormalizeFolderPath(folder.Path, projectDirectory);
            if (path is null || !seen.Add(path)) continue;
            result.Add(new SublimeTextSessionFolder(path, folder.Name));
        }

        return result;
    }

    private static string? NormalizeFolderPath(string? value, string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            var path = value.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (!Path.IsPathFullyQualified(path))
            {
                if (projectDirectory is null) return null;
                path = Path.Combine(projectDirectory, path);
            }

            path = Path.GetFullPath(path);
            if (!Directory.Exists(path)) return null;
            return TrimDirectoryEnd(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ProjectDirectory(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return null;

        try
        {
            var path = project.Trim().Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathFullyQualified(path) &&
                   string.Equals(Path.GetExtension(path), ".sublime-project", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(Path.GetFullPath(path))
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string TrimDirectoryEnd(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPlausible(int coordinate) => coordinate is >= -1_000_000 and <= 1_000_000;

    private void WarnOnce(string kind, string message)
    {
        var fingerprint = $"{kind}:{message}";
        if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;

        _lastWarnFingerprint = fingerprint;
        _log?.Warn(nameof(SublimeTextSessionReader), message);
    }

    private sealed class SessionDto
    {
        [JsonPropertyName("windows")]
        public List<WindowDto>? Windows { get; set; }
    }

    private sealed class WindowDto
    {
        [JsonPropertyName("window_id")]
        public long? WindowId { get; set; }

        [JsonPropertyName("position")]
        public string? Position { get; set; }

        [JsonPropertyName("project")]
        public string? Project { get; set; }

        [JsonPropertyName("workspace_name")]
        public string? WorkspaceName { get; set; }

        [JsonPropertyName("folders")]
        public List<FolderDto>? Folders { get; set; }
    }

    private sealed class FolderDto
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
