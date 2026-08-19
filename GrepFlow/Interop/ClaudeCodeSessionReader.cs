using System.Globalization;
using System.IO;
using System.Text.Json;

namespace GrepFlow.Interop;

public sealed record ClaudeCodeSession(
    uint ProcessId,
    string SessionId,
    string WorkingDirectory,
    ulong? ProcessStartFileTime);

public sealed class ClaudeCodeSessionReader
{
    private readonly string _configDirectory;
    private readonly Func<uint, WindowsProcessIdentity?> _readProcessIdentity;
    private readonly PluginLog? _log;
    private readonly HashSet<string> _warnedFingerprints = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ClaudeCodeSessionReader(PluginLog? log = null)
        : this(ResolveConfigDirectory(), new WindowsProcessIdentityReader().TryRead, log)
    {
    }

    public ClaudeCodeSessionReader(
        string configDirectory,
        Func<uint, WindowsProcessIdentity?> readProcessIdentity,
        PluginLog? log = null)
    {
        _configDirectory = configDirectory;
        _readProcessIdentity = readProcessIdentity;
        _log = log;
    }

    public string ConfigDirectory => _configDirectory;

    public string SessionDirectory => Path.Combine(_configDirectory, "sessions");

    public static string ResolveConfigDirectory()
        => ResolveConfigDirectory(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("USERPROFILE"));

    public static string ResolveConfigDirectory(string? configured, string? userProfile)
    {
        if (TryGetAbsolutePath(configured, out var configuredPath))
            return configuredPath;

        if (!TryGetAbsolutePath(userProfile, out var profilePath))
            profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(profilePath, ".claude");
    }

    public IReadOnlyList<ClaudeCodeSession> ReadLiveSessions()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(SessionDirectory)) return [];

                var result = new List<ClaudeCodeSession>();
                foreach (var path in Directory.EnumerateFiles(
                             SessionDirectory,
                             "*.json",
                             SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var session = ReadSession(path);
                        if (session is not null) result.Add(session);
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        WarnOnce(Path.GetFileName(path), exception);
                    }
                }

                return result;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                WarnOnce("sessions", exception);
                return [];
            }
        }
    }

    private ClaudeCodeSession? ReadSession(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;

        if (!TryGetPositiveProcessId(root, out var processId) ||
            !TryGetRequiredString(root, "sessionId", out var sessionId) ||
            !TryGetRequiredString(root, "kind", out var kind) ||
            !string.Equals(kind, "interactive", StringComparison.Ordinal) ||
            !TryGetRequiredString(root, "entrypoint", out var entrypoint) ||
            !string.Equals(entrypoint, "cli", StringComparison.Ordinal) ||
            !root.TryGetProperty("cwd", out var cwdElement))
            return null;

        var workingDirectory = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(
            cwdElement.ValueKind == JsonValueKind.String ? cwdElement.GetString() : null);
        if (workingDirectory is null) return null;

        var processStart = ParseProcessStart(root);
        if (processStart is null) return null;

        var identity = _readProcessIdentity(processId);
        if (identity is null ||
            !string.Equals(identity.ImageFileName, "claude.exe", StringComparison.OrdinalIgnoreCase) ||
            processStart is not null && identity.CreationFileTime != processStart.Value)
            return null;

        return new ClaudeCodeSession(processId, sessionId, workingDirectory, processStart);
    }

    private static bool TryGetPositiveProcessId(JsonElement root, out uint processId)
    {
        processId = 0;
        return root.TryGetProperty("pid", out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetUInt32(out processId) &&
               processId > 0;
    }

    private static bool TryGetRequiredString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static ulong? ParseProcessStart(JsonElement root)
    {
        if (!root.TryGetProperty("procStart", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return number;

        return null;
    }

    private static bool TryGetAbsolutePath(string? value, out string path)
    {
        path = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private void WarnOnce(string context, Exception exception)
    {
        var fingerprint = $"{context}:{exception.GetType().Name}:{exception.Message}";
        if (!_warnedFingerprints.Add(fingerprint)) return;

        _log?.Warn(
            nameof(ClaudeCodeSessionReader),
            $"{context}: {exception.GetType().Name}: {exception.Message}");
    }

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
            InvalidOperationException or NotSupportedException or System.Security.SecurityException;
}
