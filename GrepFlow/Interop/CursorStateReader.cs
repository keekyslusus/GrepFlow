using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GrepFlow.Interop;

public sealed class CursorStateReader
{
    internal const string GlassProjectsKey = "cursor/glass.additionalProjects";
    internal const string IdeHistoryKey = "history.recentlyOpenedPathsList";
    internal const string WorkspaceMetadataKey = "workspaceMetadata.entries";

    private readonly Func<string, string?> _readValue;

    public CursorStateReader(PluginLog? log = null)
        : this(new CursorSqliteValueReader(DefaultStatePath(), log).TryReadString)
    {
    }

    internal CursorStateReader(Func<string, string?> readValue)
    {
        _readValue = readValue;
    }

    public static string DefaultStatePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor",
            "User",
            "globalStorage",
            "state.vscdb");

    public string? TryReadActiveFolder(CursorWindowSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.WorkspaceLabel)) return null;

        return snapshot.Mode switch
        {
            CursorWindowMode.Glass => ResolveGlass(
                snapshot.WorkspaceLabel,
                _readValue(GlassProjectsKey)),
            CursorWindowMode.Ide => ResolveIde(
                snapshot.WorkspaceLabel,
                _readValue(IdeHistoryKey),
                () => _readValue(WorkspaceMetadataKey)),
            _ => null,
        };
    }

    internal static string? ResolveGlass(string? workspaceLabel, string? json)
    {
        if (IsUnavailableLabel(workspaceLabel) || string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
                return null;

            var project = document.RootElement[0];
            if (project.ValueKind != JsonValueKind.Object ||
                !TryGetString(project, "type", out var type) ||
                !string.Equals(type, "workspace", StringComparison.OrdinalIgnoreCase) ||
                HasRemoteWorkspaceMarker(project))
                return null;

            TryGetString(project, "name", out var name);
            var rawPath = FirstNonEmpty(
                GetString(project, "displayPath"),
                GetNestedString(project, "workspaceIdentifier", "uri", "fsPath"),
                GetNestedString(project, "workspaceIdentifier", "uri", "external"),
                GetNestedString(project, "workspaceIdentifier", "external"),
                GetNestedString(project, "workspaceIdentifier", "id"));
            var path = NormalizeLocalPath(rawPath);
            if (path is null) return null;

            var label = workspaceLabel!.Trim();
            var baseName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return string.Equals(name?.Trim(), label, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(baseName, label, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static string? ResolveIde(
        string? workspaceLabel,
        string? historyJson,
        Func<string?> metadataJson)
    {
        if (IsUnavailableLabel(workspaceLabel)) return null;

        var historyCandidates = ParseCandidatePaths(historyJson, "folderUri");
        var historyMatch = SelectUniqueByBaseName(historyCandidates, workspaceLabel!);
        if (historyMatch.MatchCount > 0) return historyMatch.Path;

        var metadataCandidates = ParseCandidatePaths(metadataJson(), "folderUri", "displayPath");
        return SelectUniqueByBaseName(metadataCandidates, workspaceLabel!).Path;
    }

    internal static string? NormalizeLocalPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var candidate = raw.Trim();
        string path;
        try
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri)
            {
                if (!uri.IsFile || uri.IsUnc ||
                    (!string.IsNullOrEmpty(uri.Host) &&
                     !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
                    return null;

                path = uri.LocalPath;
                if (path.Length >= 3 &&
                    (path[0] == '/' || path[0] == '\\') &&
                    char.IsAsciiLetter(path[1]) &&
                    path[2] == ':')
                    path = path[1..];
            }
            else
            {
                path = candidate;
            }
        }
        catch (UriFormatException)
        {
            return null;
        }

        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            path = $"{char.ToUpperInvariant(path[0])}{path[1..]}";
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            return null;

        try
        {
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path)) return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var root = Path.GetPathRoot(path);
        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool IsUnavailableLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return true;

        var value = label.Trim();
        return string.Equals(value, "No Repo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Select Workspace", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseCandidatePaths(string? json, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            var raw = new List<string>();
            CollectCandidateProperties(document.RootElement, propertyNames, raw);
            return raw
                .Select(NormalizeLocalPath)
                .Where(path => path is not null)
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void CollectCandidateProperties(
        JsonElement element,
        IReadOnlyCollection<string> propertyNames,
        ICollection<string> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var belongsToRemoteWorkspace = HasRemoteWorkspaceMarker(element);
            foreach (var property in element.EnumerateObject())
            {
                if (!belongsToRemoteWorkspace &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
                }

                CollectCandidateProperties(property.Value, propertyNames, values);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectCandidateProperties(item, propertyNames, values);
        }
    }

    private static bool HasRemoteWorkspaceMarker(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (string.Equals(property.Name, "remoteAuthority", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(value))
                        return true;

                    if (string.Equals(property.Name, "scheme", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(value) &&
                        !string.Equals(value, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (IsWorkspaceUriProperty(property.Name) &&
                        !IsLocalOpaqueWorkspaceId(property.Name, value) &&
                        IsRemoteUri(value))
                        return true;
                }

                if (HasRemoteWorkspaceMarker(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasRemoteWorkspaceMarker(item)) return true;
            }
        }

        return false;
    }

    private static bool IsWorkspaceUriProperty(string propertyName)
        => string.Equals(propertyName, "uri", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "folderUri", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "external", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "id", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalOpaqueWorkspaceId(string propertyName, string? value)
        => string.Equals(propertyName, "id", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(value) &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "workspace", StringComparison.OrdinalIgnoreCase);

    private static bool IsRemoteUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.IsAbsoluteUri)
            return false;

        return !uri.IsFile || uri.IsUnc ||
            (!string.IsNullOrEmpty(uri.Host) &&
             !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static (string? Path, int MatchCount) SelectUniqueByBaseName(
        IEnumerable<string> candidates,
        string workspaceLabel)
    {
        var matches = candidates
            .Where(path => string.Equals(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                workspaceLabel.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? (matches[0], 1) : (null, matches.Length);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? GetString(JsonElement element, string propertyName)
        => TryGetString(element, propertyName, out var value) ? value : null;

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? GetNestedString(JsonElement element, params string[] propertyPath)
    {
        foreach (var propertyName in propertyPath)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out element))
                return null;
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}

internal sealed class CursorSqliteValueReader
{
    private readonly string _databasePath;
    private readonly PluginLog? _log;
    private readonly object _warningGate = new();
    private string? _lastWarnFingerprint;

    public CursorSqliteValueReader(string databasePath, PluginLog? log = null)
    {
        _databasePath = databasePath;
        _log = log;
    }

    public string? TryReadString(string key)
    {
        if (!File.Exists(_databasePath))
        {
            WarnOnce("missing", $"Cursor state database was not found: {_databasePath}");
            return null;
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            var pathBytes = SqliteNative.Utf8(_databasePath);
            var result = SqliteNative.sqlite3_open_v2(
                pathBytes,
                out database,
                SqliteNative.OpenReadonly,
                IntPtr.Zero);
            if (result != SqliteNative.Ok)
            {
                WarnOnce($"open:{result}", SqliteNative.ErrorMessage(database, result));
                return null;
            }

            _ = SqliteNative.sqlite3_busy_timeout(database, SqliteNative.BusyTimeoutMilliseconds);
            var sql = SqliteNative.Utf8("SELECT value FROM ItemTable WHERE key = ?1 LIMIT 1");
            result = SqliteNative.sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero);
            if (result != SqliteNative.Ok)
            {
                WarnOnce($"prepare:{result}", SqliteNative.ErrorMessage(database, result));
                return null;
            }

            var keyBytes = SqliteNative.Utf8(key);
            result = SqliteNative.sqlite3_bind_text(
                statement,
                1,
                keyBytes,
                keyBytes.Length - 1,
                SqliteNative.Transient);
            if (result != SqliteNative.Ok)
            {
                WarnOnce($"bind:{result}", SqliteNative.ErrorMessage(database, result));
                return null;
            }

            result = SqliteNative.sqlite3_step(statement);
            if (result != SqliteNative.Row)
            {
                if (result != SqliteNative.Done)
                    WarnOnce($"step:{result}", SqliteNative.ErrorMessage(database, result));
                return null;
            }

            var length = SqliteNative.sqlite3_column_bytes(statement, 0);
            var pointer = SqliteNative.sqlite3_column_text(statement, 0);
            if (pointer == IntPtr.Zero || length < 0) return null;

            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (DllNotFoundException exception)
        {
            WarnOnce("dll", exception.Message);
            return null;
        }
        catch (EntryPointNotFoundException exception)
        {
            WarnOnce("entry", exception.Message);
            return null;
        }
        catch (BadImageFormatException exception)
        {
            WarnOnce("abi", exception.Message);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            WarnOnce("access", exception.Message);
            return null;
        }
        catch (IOException exception)
        {
            WarnOnce("io", exception.Message);
            return null;
        }
        finally
        {
            TryRelease(statement, database);
        }
    }

    private static void TryRelease(IntPtr statement, IntPtr database)
    {
        try
        {
            if (statement != IntPtr.Zero) _ = SqliteNative.sqlite3_finalize(statement);
            if (database != IntPtr.Zero) _ = SqliteNative.sqlite3_close_v2(database);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (BadImageFormatException)
        {
        }
    }

    private void WarnOnce(string kind, string message)
    {
        var fingerprint = $"{kind}:{message}";
        lock (_warningGate)
        {
            if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;
            _lastWarnFingerprint = fingerprint;
        }

        _log?.Warn(nameof(CursorStateReader), message);
    }
}
