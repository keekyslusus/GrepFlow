using System.IO;
using System.Text.Json;

namespace GrepFlow.Interop;

public sealed record CodexCliSession(
    string SessionId,
    string WorkingDirectory,
    DateTime LastActivityUtc,
    string? InitialWorkingDirectory = null);

public sealed class CodexCliSessionReader
{
    private const string CoordinationLockName = ".coordination.lock";
    private const int TailBlockBytes = 64 * 1024;
    internal const int MaxJsonLineBytes = 256 * 1024;
    // Rollouts grow indefinitely, so one launcher query must never parse the full history.
    internal const int MaxTailBytes = 4 * 1024 * 1024;

    private readonly string _codexHome;
    private readonly PluginLog? _log;
    private readonly Dictionary<string, string> _rolloutPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedFingerprints = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public CodexCliSessionReader(PluginLog? log = null)
        : this(ResolveCodexHome(), log)
    {
    }

    public CodexCliSessionReader(string codexHome, PluginLog? log = null)
    {
        _codexHome = codexHome;
        _log = log;
    }

    public string CodexHome => _codexHome;

    public static string ResolveCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (IsExistingAbsoluteDirectory(configured))
            return Path.GetFullPath(configured!);

        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(profile))
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(profile, ".codex");
    }

    public IReadOnlyList<CodexCliSession> ReadActiveSessions()
    {
        lock (_gate)
        {
            try
            {
                return ReadActiveSessionsCore();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                WarnOnce("sessions", exception);
                return [];
            }
        }
    }

    public CodexCliSession? ReadActiveSession(string sessionId)
    {
        lock (_gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sessionId) ||
                    !string.Equals(Path.GetFileName(sessionId), sessionId, StringComparison.Ordinal))
                    return null;

                var lockPath = Path.Combine(_codexHome, "thread-writer-locks", $"{sessionId}.lock");
                var sessionsDirectory = Path.Combine(_codexHome, "sessions");
                if (!File.Exists(lockPath) || !Directory.Exists(sessionsDirectory)) return null;

                return ReadSession(sessionsDirectory, lockPath, sessionId);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                WarnOnce(sessionId, exception);
                return null;
            }
        }
    }

    private IReadOnlyList<CodexCliSession> ReadActiveSessionsCore()
    {
        var lockDirectory = Path.Combine(_codexHome, "thread-writer-locks");
        var sessionsDirectory = Path.Combine(_codexHome, "sessions");
        if (!Directory.Exists(lockDirectory) || !Directory.Exists(sessionsDirectory)) return [];

        var result = new List<CodexCliSession>();
        foreach (var lockPath in Directory.EnumerateFiles(lockDirectory, "*.lock", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(lockPath), CoordinationLockName, StringComparison.OrdinalIgnoreCase))
                continue;

            var sessionId = Path.GetFileNameWithoutExtension(lockPath);
            if (string.IsNullOrWhiteSpace(sessionId)) continue;

            try
            {
                var session = ReadSession(sessionsDirectory, lockPath, sessionId);
                if (session is not null) result.Add(session);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                WarnOnce(sessionId, exception);
            }
        }

        return result;
    }

    private CodexCliSession? ReadSession(string sessionsDirectory, string lockPath, string sessionId)
    {
        var rolloutPath = FindRolloutPath(sessionsDirectory, sessionId);
        if (rolloutPath is null) return null;

        var workingDirectories = ReadWorkingDirectories(rolloutPath);
        if (workingDirectories is null) return null;

        return new CodexCliSession(
            sessionId,
            workingDirectories.Value.Effective,
            File.GetLastWriteTimeUtc(lockPath),
            workingDirectories.Value.Initial);
    }

    private string? FindRolloutPath(string sessionsDirectory, string sessionId)
    {
        if (_rolloutPaths.TryGetValue(sessionId, out var cached) && File.Exists(cached))
            return cached;

        _rolloutPaths.Remove(sessionId);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        var suffix = $"{sessionId}.jsonl";
        foreach (var path in Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", options))
        {
            if (!Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            _rolloutPaths[sessionId] = path;
            return path;
        }

        return null;
    }

    private static (string Initial, string Effective)? ReadWorkingDirectories(string rolloutPath)
    {
        var initialWorkingDirectory = ReadInitialWorkingDirectory(rolloutPath);
        if (initialWorkingDirectory is null) return null;

        return (
            initialWorkingDirectory,
            ReadLatestTurnWorkingDirectory(rolloutPath) ?? initialWorkingDirectory);
    }

    private static string? ReadInitialWorkingDirectory(string rolloutPath)
    {
        using var stream = OpenRollout(rolloutPath);
        using var reader = new StreamReader(stream);
        var firstLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(firstLine)) return null;

        using var json = JsonDocument.Parse(firstLine);
        var root = json.RootElement;
        if (!root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal) ||
            !root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("source", out var source) ||
            source.ValueKind != JsonValueKind.String ||
            !string.Equals(source.GetString(), "cli", StringComparison.Ordinal) ||
            !payload.TryGetProperty("cwd", out var cwd) ||
            cwd.ValueKind != JsonValueKind.String)
            return null;

        return NormalizeLocalDirectory(cwd.GetString());
    }

    private static string? ReadLatestTurnWorkingDirectory(string rolloutPath)
    {
        using var stream = OpenRollout(rolloutPath);
        if (stream.Length == 0) return null;

        var minimumPosition = Math.Max(0, stream.Length - MaxTailBytes);
        var position = stream.Length;
        var block = new byte[TailBlockBytes];
        byte[] suffix = [];
        var discardingOversizedLine = false;
        while (position > minimumPosition)
        {
            var count = (int)Math.Min(TailBlockBytes, position - minimumPosition);
            var blockStart = position - count;
            stream.Seek(blockStart, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var blockRead = stream.Read(block, read, count - read);
                if (blockRead == 0) break;
                read += blockRead;
            }
            if (read != count) return null;

            var lineEnd = count;
            var rightmostLine = true;
            var foundLineBreak = false;
            for (var index = count - 1; index >= 0; index--)
            {
                if (block[index] != (byte)'\n') continue;

                foundLineBreak = true;
                var segment = block.AsSpan(index + 1, lineEnd - index - 1);
                string? selected;
                if (rightmostLine)
                {
                    selected = discardingOversizedLine
                        ? null
                        : TryReadSplitTurnWorkingDirectory(segment, suffix);
                    suffix = [];
                    discardingOversizedLine = false;
                    rightmostLine = false;
                }
                else
                {
                    selected = segment.Length <= MaxJsonLineBytes
                        ? TryReadTurnWorkingDirectory(segment)
                        : null;
                }

                if (selected is not null) return selected;
                lineEnd = index;
            }

            var prefix = block.AsSpan(0, lineEnd);
            if (foundLineBreak)
            {
                discardingOversizedLine = prefix.Length > MaxJsonLineBytes;
                suffix = discardingOversizedLine ? [] : prefix.ToArray();
            }
            else if (!discardingOversizedLine)
            {
                if (prefix.Length + suffix.Length > MaxJsonLineBytes)
                {
                    suffix = [];
                    discardingOversizedLine = true;
                }
                else
                {
                    var joined = new byte[prefix.Length + suffix.Length];
                    prefix.CopyTo(joined);
                    suffix.CopyTo(joined, prefix.Length);
                    suffix = joined;
                }
            }

            position = blockStart;
        }

        return minimumPosition == 0 && !discardingOversizedLine
            ? TryReadTurnWorkingDirectory(suffix)
            : null;
    }

    private static string? TryReadSplitTurnWorkingDirectory(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> suffix)
    {
        if (prefix.Length + suffix.Length > MaxJsonLineBytes) return null;
        if (suffix.IsEmpty) return TryReadTurnWorkingDirectory(prefix);

        var line = new byte[prefix.Length + suffix.Length];
        prefix.CopyTo(line);
        suffix.CopyTo(line.AsSpan(prefix.Length));
        return TryReadTurnWorkingDirectory(line);
    }

    private static string? TryReadTurnWorkingDirectory(ReadOnlySpan<byte> lineBytes)
    {
        if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r') lineBytes = lineBytes[..^1];
        if (lineBytes.IsEmpty) return null;

        try
        {
            var reader = new Utf8JsonReader(lineBytes);
            using var json = JsonDocument.ParseValue(ref reader);
            if (reader.Read()) return null;

            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "turn_context", StringComparison.Ordinal) ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("cwd", out var cwd) ||
                cwd.ValueKind != JsonValueKind.String)
                return null;

            return NormalizeLocalDirectory(cwd.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FileStream OpenRollout(string rolloutPath)
        => new(
            rolloutPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private static string? NormalizeLocalDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            !Directory.Exists(value))
            return null;

        var path = Path.GetFullPath(value);
        return path.Length > 3 ? Path.TrimEndingDirectorySeparator(path) : path;
    }

    private static bool IsExistingAbsoluteDirectory(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Path.IsPathFullyQualified(value) &&
                   Directory.Exists(value);
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
            nameof(CodexCliSessionReader),
            $"{context}: {exception.GetType().Name}: {exception.Message}");
    }

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
            InvalidOperationException or NotSupportedException or System.Security.SecurityException;
}
