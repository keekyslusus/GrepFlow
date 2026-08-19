using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrepFlow.Interop;

public sealed class FilePilotSessionReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _sessionPath;
    private readonly PluginLog? _log;

    private DateTime _cachedWriteTimeUtc;
    private string? _cachedPath;
    private string? _lastWarnFingerprint;

    public FilePilotSessionReader(PluginLog? log = null)
        : this(DefaultSessionPath(), log)
    {
    }

    public FilePilotSessionReader(string sessionPath, PluginLog? log = null)
    {
        _sessionPath = sessionPath;
        _log = log;
    }

    public static string DefaultSessionPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Voidstar",
            "FilePilot",
            "FPilot-Session.json");

    public string? TryReadSelectedPanelPath()
    {
        try
        {
            if (!File.Exists(_sessionPath)) return null;

            var writeTimeUtc = File.GetLastWriteTimeUtc(_sessionPath);
            if (_cachedPath is not null && writeTimeUtc == _cachedWriteTimeUtc)
                return Directory.Exists(_cachedPath) ? _cachedPath : null;

            var json = ReadFileWithShare();
            if (json is null) return null;

            var path = ParseSelectedPanelPath(json);
            if (path is null) return null;

            _cachedWriteTimeUtc = writeTimeUtc;
            _cachedPath = path;
            return path;
        }
        catch (IOException exception)
        {
            WarnOnce("io", exception.Message);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            WarnOnce("access", exception.Message);
            return null;
        }
        catch (JsonException exception)
        {
            WarnOnce("json", exception.Message);
            return null;
        }
    }

    private string? ReadFileWithShare()
    {
        // FileShare.ReadWrite: File Pilot keeps the session file open for write
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    _sessionPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(10);
            }
        }

        WarnOnce("share", "could not open session file for reading");
        return null;
    }

    private string? ParseSelectedPanelPath(string json)
    {
        var session = JsonSerializer.Deserialize<SessionDto>(json, JsonOptions);
        if (session is null) return null;

        // 0.8.x nests under Layout; root-level fields are a defensive fallback
        var panels = session.Layout?.Panels ?? session.Panels;
        var selectedPanel = session.Layout?.SelectedPanel ?? session.SelectedPanel;
        if (panels is null || panels.Count == 0) return null;

        SessionPanelDto? panel = null;
        foreach (var candidate in panels)
        {
            if (candidate.Id == selectedPanel)
            {
                panel = candidate;
                break;
            }
        }

        if (panel is null) return null;

        var raw = panel.Path;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var path = NormalizePath(raw);
        if (path is null || !Directory.Exists(path)) return null;

        return path;
    }

    private static string? NormalizePath(string raw)
    {
        var path = raw.Replace('/', '\\').Trim();
        if (path.Length == 0) return null;

        // length > 3 keeps drive roots like "C:\"
        if (path.Length > 3) path = path.TrimEnd('\\');
        return path.Length == 0 ? null : path;
    }

    private void WarnOnce(string kind, string message)
    {
        var fingerprint = $"{kind}:{message}";
        if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;

        _lastWarnFingerprint = fingerprint;
        _log?.Warn(nameof(FilePilotSessionReader), message);
    }

    private sealed class SessionDto
    {
        [JsonPropertyName("Layout")]
        public LayoutDto? Layout { get; set; }

        [JsonPropertyName("SelectedPanel")]
        public int? SelectedPanel { get; set; }

        [JsonPropertyName("Panels")]
        public List<SessionPanelDto>? Panels { get; set; }
    }

    private sealed class LayoutDto
    {
        [JsonPropertyName("SelectedPanel")]
        public int SelectedPanel { get; set; }

        [JsonPropertyName("Panels")]
        public List<SessionPanelDto>? Panels { get; set; }
    }

    private sealed class SessionPanelDto
    {
        [JsonPropertyName("ID")]
        public int Id { get; set; }

        [JsonPropertyName("Path")]
        public string? Path { get; set; }
    }
}
