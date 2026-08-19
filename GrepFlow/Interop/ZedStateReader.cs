using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GrepFlow.Interop;

internal sealed record ZedWorkspaceEntry(string Paths);

internal sealed class ZedStateReader
{
    private const string ItemDelimiter = " \u2014 ";
    private static readonly string[] CollaborationMarkers = [" \u2199", " \u2197"];

    private readonly Func<IReadOnlyList<ZedWorkspaceEntry>?> _readEntries;
    private readonly Func<string, bool> _directoryExists;

    public ZedStateReader(PluginLog? log = null)
        : this(new ZedSqliteWorkspaceReader(DefaultStatePath(), log).TryReadEntries, Directory.Exists)
    {
    }

    internal ZedStateReader(
        Func<IReadOnlyList<ZedWorkspaceEntry>?> readEntries,
        Func<string, bool>? directoryExists = null)
    {
        _readEntries = readEntries;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public static string DefaultStatePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zed",
            "db",
            "0-stable",
            "db.sqlite");

    public string? TryReadActiveFolder(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var entries = _readEntries();
        if (entries is null) return null;

        var matches = entries
            .Select(entry => NormalizeSingleRoot(entry.Paths))
            .Where(path => path is not null && TitleMatches(title, DisplayedRootName(path)))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return matches.Length == 1 && _directoryExists(matches[0]) ? matches[0] : null;
    }

    internal static string? NormalizeSingleRoot(string? serializedPaths)
    {
        if (serializedPaths is null) return null;

        var paths = serializedPaths.Split('\n');
        if (paths.Length != 1 || paths[0].Length == 0) return null;

        var candidate = paths[0];
        if (!Path.IsPathFullyQualified(candidate) ||
            candidate.StartsWith("\\\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal))
            return null;

        try
        {
            var path = Path.GetFullPath(candidate);
            var root = Path.GetPathRoot(path);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                ? path
                : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
        catch (IOException)
        {
            return null;
        }
    }

    internal static bool TitleMatches(string title, string? displayedRootName)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(displayedRootName)) return false;
        if (TitleMatchesWithoutMarker(title, displayedRootName)) return true;

        foreach (var marker in CollaborationMarkers)
        {
            if (title.EndsWith(marker, StringComparison.Ordinal) &&
                TitleMatchesWithoutMarker(title[..^marker.Length], displayedRootName))
                return true;
        }

        return false;
    }

    private static bool TitleMatchesWithoutMarker(string title, string displayedRootName)
    {
        if (string.Equals(title, displayedRootName, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = displayedRootName + ItemDelimiter;
        return title.Length > prefix.Length &&
            title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DisplayedRootName(string? path)
    {
        if (path is null) return null;
        var root = Path.GetPathRoot(path);
        var withoutTrailingSeparator = string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(withoutTrailingSeparator);
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

internal sealed class ZedSqliteWorkspaceReader
{
    private const string Query = """
        SELECT paths
        FROM workspaces
        WHERE paths IS NOT NULL
          AND remote_connection_id IS NULL
        """;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly string _databasePath;
    private readonly PluginLog? _log;
    private readonly object _warningGate = new();
    private string? _lastWarnFingerprint;

    public ZedSqliteWorkspaceReader(string databasePath, PluginLog? log = null)
    {
        _databasePath = databasePath;
        _log = log;
    }

    public IReadOnlyList<ZedWorkspaceEntry>? TryReadEntries()
    {
        if (!File.Exists(_databasePath))
        {
            WarnOnce("missing", $"Zed state database was not found: {_databasePath}");
            return null;
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            var result = SqliteNative.sqlite3_open_v2(
                SqliteNative.Utf8(_databasePath),
                out database,
                SqliteNative.OpenReadonly,
                IntPtr.Zero);
            if (result != SqliteNative.Ok)
            {
                WarnOnce($"open:{result}", SqliteNative.ErrorMessage(database, result));
                return null;
            }

            _ = SqliteNative.sqlite3_busy_timeout(database, SqliteNative.BusyTimeoutMilliseconds);
            result = SqliteNative.sqlite3_prepare_v2(
                database,
                SqliteNative.Utf8(Query),
                -1,
                out statement,
                IntPtr.Zero);
            if (result != SqliteNative.Ok)
            {
                WarnOnce($"prepare:{result}", SqliteNative.ErrorMessage(database, result));
                return null;
            }

            var entries = new List<ZedWorkspaceEntry>();
            while (true)
            {
                result = SqliteNative.sqlite3_step(statement);
                if (result == SqliteNative.Done) return entries;
                if (result != SqliteNative.Row)
                {
                    WarnOnce($"step:{result}", SqliteNative.ErrorMessage(database, result));
                    return null;
                }

                var length = SqliteNative.sqlite3_column_bytes(statement, 0);
                var pointer = SqliteNative.sqlite3_column_text(statement, 0);
                if (pointer == IntPtr.Zero || length < 0) return null;

                var bytes = new byte[length];
                if (length > 0) Marshal.Copy(pointer, bytes, 0, length);
                entries.Add(new ZedWorkspaceEntry(StrictUtf8.GetString(bytes)));
            }
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
        catch (DecoderFallbackException exception)
        {
            WarnOnce("utf8", exception.Message);
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

        _log?.Warn(nameof(ZedStateReader), message);
    }
}
