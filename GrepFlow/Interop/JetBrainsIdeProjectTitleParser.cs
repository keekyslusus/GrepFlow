using System.IO;

namespace GrepFlow.Interop;

internal static class JetBrainsIdeProjectTitleParser
{
    internal const string TitleSeparator = " – ";

    public static string? TryGetExplicitProjectPath(string? title, string userHome)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var searchBefore = title.Length - 1;
        while (searchBefore >= 0)
        {
            var closeBracket = title.LastIndexOf(']', searchBefore);
            if (closeBracket < 0) break;

            var openBracket = title.LastIndexOf('[', closeBracket);
            if (openBracket < 0) return null;

            var suffix = title[(closeBracket + 1)..];
            if (suffix.Length > 0 && !suffix.StartsWith(TitleSeparator, StringComparison.Ordinal))
            {
                searchBefore = openBracket - 1;
                continue;
            }

            var raw = title[(openBracket + 1)..closeBracket].Trim();
            if (raw == "~")
                raw = userHome;
            else if (raw.StartsWith("~/", StringComparison.Ordinal) ||
                     raw.StartsWith("~\\", StringComparison.Ordinal))
                raw = Path.Combine(userHome, raw[2..]);

            var path = NormalizeLocalPath(raw);
            if (path is not null) return path;

            searchBefore = openBracket - 1;
        }

        return null;
    }

    public static string? MatchKnownProjectName(string? title, IEnumerable<string> knownNames)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        string? best = null;
        foreach (var name in knownNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var exact = string.Equals(title, name, StringComparison.OrdinalIgnoreCase);
            var prefix = title.StartsWith(name + TitleSeparator, StringComparison.OrdinalIgnoreCase);
            if (!exact && !prefix) continue;

            if (best is null || name.Length > best.Length)
                best = name;
        }

        return best;
    }

    internal static string? NormalizeLocalPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var path = raw.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/'))
            return null;

        try
        {
            path = Path.GetFullPath(path);
            var root = Path.GetPathRoot(path);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                ? path
                : path.TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
