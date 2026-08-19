using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrepFlow.Interop;

public sealed class VisualStudioCodeSessionReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _statePath;
    private readonly PluginLog? _log;

    private string? _lastWarnFingerprint;

    public VisualStudioCodeSessionReader(PluginLog? log = null)
        : this(DefaultStatePath(), log)
    {
    }

    public VisualStudioCodeSessionReader(string statePath, PluginLog? log = null)
    {
        _statePath = statePath;
        _log = log;
    }

    public static string DefaultStatePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Code",
            "User",
            "globalStorage",
            "storage.json");

    public string? TryReadLastActiveFolder()
    {
        if (!File.Exists(_statePath)) return null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var state = JsonSerializer.Deserialize<StateDto>(ReadStateFile(), JsonOptions);
                return NormalizeLocalFolder(state?.WindowsState?.LastActiveWindow?.Folder);
            }
            catch (IOException exception)
            {
                if (attempt == 2)
                    WarnOnce("io", exception.Message);
            }
            catch (JsonException exception)
            {
                if (attempt == 2)
                    WarnOnce("json", exception.Message);
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

    private string ReadStateFile()
    {
        using var stream = new FileStream(
            _statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? NormalizeLocalFolder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !uri.IsFile ||
            uri.IsUnc ||
            (!string.IsNullOrEmpty(uri.Host) &&
             !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            return null;

        var path = uri.LocalPath;
        if (path.Length >= 3 &&
            (path[0] == '/' || path[0] == '\\') &&
            char.IsAsciiLetter(path[1]) &&
            path[2] == ':')
            path = $"{char.ToUpperInvariant(path[1])}{path[2..]}";

        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return null;

        return path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar) : path;
    }

    private void WarnOnce(string kind, string message)
    {
        var fingerprint = $"{kind}:{message}";
        if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;

        _lastWarnFingerprint = fingerprint;
        _log?.Warn(nameof(VisualStudioCodeSessionReader), message);
    }

    private sealed class StateDto
    {
        [JsonPropertyName("windowsState")]
        public WindowsStateDto? WindowsState { get; set; }
    }

    private sealed class WindowsStateDto
    {
        [JsonPropertyName("lastActiveWindow")]
        public WindowDto? LastActiveWindow { get; set; }
    }

    private sealed class WindowDto
    {
        [JsonPropertyName("folder")]
        public string? Folder { get; set; }
    }
}
