using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace GrepFlow.Interop;

internal sealed record CodexWindowEvidence(
    bool HasProductMarker,
    string? ThreadId,
    string? VisibleWorkingDirectory,
    string? VisiblePathHint,
    IReadOnlyList<string> TitleMatches);

public sealed partial class CodexCodeWindowMatcher
{
    private const int MaxTextLength = 32 * 1024;
    private const int MaxHeaderLines = 20;
    private const int MaxFooterLines = 12;
    private const int MaxProjectTitleGraphemes = 24;
    private const int MaxThreadTitleGraphemes = 32;
    private const int MaxAliasCacheEntries = 64;
    private const string ActivityIndicators = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    private readonly Dictionary<string, IReadOnlyList<string>> _projectAliasCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _aliasCacheGate = new();
    private int _projectAliasProbeCount;

    internal int ProjectAliasProbeCount => Volatile.Read(ref _projectAliasProbeCount);

    public string? Match(string? visibleText, IEnumerable<string> titleMatches)
    {
        var candidates = titleMatches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        if (candidates.Length != 1) return null;
        if (HasProductMarker(visibleText)) return candidates[0];

        var lines = GetLines(visibleText);
        var visible = lines is null
            ? null
            : FindVisibleWorkingDirectory(
                lines,
                candidates,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return string.Equals(visible, candidates[0], StringComparison.OrdinalIgnoreCase)
            ? candidates[0]
            : null;
    }

    public bool HasProductMarker(string? visibleText)
    {
        var lines = GetLines(visibleText);
        return lines is not null && lines
            .Take(MaxHeaderLines)
            .Any(line => line.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase));
    }

    internal CodexWindowEvidence Analyze(
        string? visibleText,
        string? title,
        IEnumerable<string> candidatePaths,
        IEnumerable<string> activeSessionIds,
        string? userProfile,
        bool includeProjectAliases = false)
    {
        var candidates = candidatePaths
            .Select(WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var lines = GetLines(visibleText);
        var visibleWorkingDirectory = lines is null
            ? null
            : FindVisibleWorkingDirectory(lines, candidates, userProfile);
        var visiblePathHint = lines is null
            ? null
            : FindVisiblePathHint(lines, userProfile);
        var titleMatches = FindTitleMatches(title, candidates, includeProjectAliases);

        return new CodexWindowEvidence(
            HasProductMarker(visibleText),
            FindActiveThreadId(title, activeSessionIds),
            visibleWorkingDirectory,
            visiblePathHint,
            titleMatches);
    }

    internal static string? FindActiveThreadId(string? title, IEnumerable<string> activeSessionIds)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var matches = activeSessionIds
            .Where(value => Guid.TryParse(value, out _))
            .Select(value => new { Original = value, Normalized = Guid.Parse(value).ToString("D") })
            .Where(value => ContainsWholeThreadToken(title, value.Normalized) ||
                            ContainsWholeThreadToken(title, TruncateTitlePart(
                                value.Normalized,
                                MaxThreadTitleGraphemes)))
            .Select(value => value.Original)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool HasUuidCandidate(string? title)
        => !string.IsNullOrWhiteSpace(title) &&
           (UuidTokenRegex().IsMatch(title) || TruncatedUuidTokenRegex().IsMatch(title));

    internal IReadOnlyList<string> FindTitleMatches(
        string? title,
        IEnumerable<string> candidatePaths,
        bool includeProjectAliases)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];

        return candidatePaths
            .Where(path => TitleMatchesWorkingDirectory(title, path, includeProjectAliases))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal bool TitleMatchesWorkingDirectory(
        string title,
        string workingDirectory,
        bool includeProjectAliases)
        => GetTitleAliases(workingDirectory, includeProjectAliases)
            .Any(alias => ContainsWholeTitleToken(title, alias));

    internal IReadOnlyList<string> GetProjectTitleAliases(string workingDirectory)
        => GetTitleAliases(workingDirectory, includeProjectAliases: true);

    private IReadOnlyList<string> GetTitleAliases(string workingDirectory, bool includeProjectAliases)
    {
        var normalized = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(workingDirectory);
        if (normalized is null) return [];

        var names = new List<string>();
        var cwdName = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
        if (!string.IsNullOrWhiteSpace(cwdName)) names.Add(cwdName);

        if (includeProjectAliases)
        {
            lock (_aliasCacheGate)
            {
                if (_projectAliasCache.TryGetValue(normalized, out var cached))
                    return cached;
            }

            Interlocked.Increment(ref _projectAliasProbeCount);
            var projectRoot = FindProjectRoot(normalized);
            if (projectRoot is not null)
            {
                var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRoot));
                if (!string.IsNullOrWhiteSpace(rootName)) names.Add(rootName);
            }
        }

        var aliases = names
            .SelectMany(name => new[] { name, TruncateProjectName(name) })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (includeProjectAliases)
        {
            lock (_aliasCacheGate)
            {
                if (_projectAliasCache.Count >= MaxAliasCacheEntries)
                    _projectAliasCache.Remove(_projectAliasCache.Keys.First());
                _projectAliasCache[normalized] = aliases;
            }
        }

        return aliases;
    }

    internal static string TruncateProjectName(string value)
        => TruncateTitlePart(value, MaxProjectTitleGraphemes);

    internal static string ThreadTitleValue(string sessionId)
        => TruncateTitlePart(Guid.Parse(sessionId).ToString("D"), MaxThreadTitleGraphemes);

    private static string? FindVisibleWorkingDirectory(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> candidates,
        string? userProfile)
    {
        var footerLine = FindFooterLine(lines);
        if (footerLine is null || !IsReliableFooterLine(footerLine, userProfile)) return null;

        var visiblePaths = FindPathHints(footerLine, userProfile);
        var matches = candidates
            .Where(candidate => visiblePaths.Any(path => string.Equals(
                path,
                candidate,
                StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? FindVisiblePathHint(IReadOnlyList<string> lines, string? userProfile)
    {
        var footerLine = FindFooterLine(lines);
        if (footerLine is null || !IsReliableFooterLine(footerLine, userProfile)) return null;

        var matches = FindPathHints(footerLine, userProfile);
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? FindFooterLine(IReadOnlyList<string> lines)
        => lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(MaxFooterLines)
            .LastOrDefault(line => !LooksLikeShellPrompt(line));

    private static string[] FindPathHints(string line, string? userProfile)
        => SplitStatusLineItems(line)
            .Select(item => ExpandHomeRelativePath(item, userProfile))
            .Select(WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

    private static bool IsReliableFooterLine(string line, string? userProfile)
    {
        if (LooksLikeConversationLine(line)) return false;

        var items = SplitStatusLineItems(line).ToArray();
        if (items.Length == 0 || FindPathHints(line, userProfile).Length == 0) return false;
        return items.Length == 1 || items.Any(IsRecognizedStatusItem);
    }

    private static bool IsRecognizedStatusItem(string item)
    {
        var value = item.Trim();
        return value.Equals("current-dir", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("model ", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("codex", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Context ", StringComparison.OrdinalIgnoreCase) && value.Contains('%') ||
               IsReasoningValue(value);
    }

    private static IEnumerable<string> SplitStatusLineItems(string line)
        => line.Split(" \u00b7 ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? ExpandHomeRelativePath(string item, string? userProfile)
    {
        var value = item.Trim();
        if (value.EndsWith('>') || value.Contains("...", StringComparison.Ordinal)) return null;
        if (value.StartsWith("~\\", StringComparison.Ordinal) || value.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(userProfile);
            if (home is null) return null;
            value = Path.Combine(home, value[2..].Replace('/', Path.DirectorySeparatorChar));
        }

        return value.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? FindProjectRoot(string workingDirectory)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(workingDirectory);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }

        string? codexRoot = null;
        while (current is not null)
        {
            try
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    File.Exists(Path.Combine(current.FullName, ".git")))
                    return current.FullName;
                if (codexRoot is null && File.Exists(Path.Combine(current.FullName, ".codex", "config.toml")))
                    codexRoot = current.FullName;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return null;
            }

            current = current.Parent;
        }

        return codexRoot;
    }

    private static bool ContainsWholeTitleToken(string title, string alias)
        => title
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => TitleItemMatchesAlias(item, alias));

    private static bool TitleItemMatchesAlias(string item, string alias)
    {
        if (string.Equals(item, alias, StringComparison.OrdinalIgnoreCase)) return true;
        for (var index = 0; index < item.Length; index++)
        {
            if (!ActivityIndicators.Contains(item[index])) continue;
            if (index > 0 && item[index - 1] != ' ' ||
                index + 1 < item.Length && item[index + 1] != ' ')
                continue;

            var before = item[..index].Trim();
            var after = item[(index + 1)..].Trim();
            if (before.Length == 0 || after.Length == 0)
            {
                var project = before.Length == 0 ? after : before;
                return string.Equals(project, alias, StringComparison.OrdinalIgnoreCase);
            }

            if ((!IsRunStateValue(before) && string.Equals(before, alias, StringComparison.OrdinalIgnoreCase)) ||
                (!IsRunStateValue(after) && string.Equals(after, alias, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static bool IsRunStateValue(string value)
        => value.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("Starting", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("Working", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("Waiting", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("Thinking", StringComparison.OrdinalIgnoreCase);

    private static bool IsReasoningValue(string value)
        => value.Equals("minimal", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("low", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("high", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("xhigh", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("max", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("ultra", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWholeThreadToken(string title, string token)
    {
        var index = title.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !IsThreadTokenCharacter(title[index - 1]);
            var afterIndex = index + token.Length;
            var after = afterIndex == title.Length || !IsThreadTokenCharacter(title[afterIndex]);
            if (before && after) return true;
            index = title.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string TruncateTitlePart(string value, int maxGraphemes)
    {
        var elements = StringInfo.GetTextElementEnumerator(value);
        var graphemes = new List<string>();
        while (elements.MoveNext()) graphemes.Add(elements.GetTextElement());
        if (graphemes.Count <= maxGraphemes) return value;

        return string.Concat(graphemes.Take(maxGraphemes - 3)) + "...";
    }

    private static IReadOnlyList<string>? GetLines(string? visibleText)
    {
        if (string.IsNullOrWhiteSpace(visibleText) ||
            visibleText.Length > MaxTextLength ||
            visibleText.IndexOf('\0') >= 0)
            return null;

        return visibleText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool LooksLikeShellPrompt(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("PS ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith('>');
    }

    private static bool LooksLikeConversationLine(string line)
    {
        var firstItem = line.Split(" \u00b7 ", 2, StringSplitOptions.TrimEntries)[0].TrimEnd(':');
        return firstItem.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
               firstItem.Equals("user", StringComparison.OrdinalIgnoreCase) ||
               firstItem.Equals("system", StringComparison.OrdinalIgnoreCase) ||
               firstItem.Equals("developer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsThreadTokenCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '-' or '.';

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Security.SecurityException;

    [GeneratedRegex(@"(?<![A-Za-z0-9-])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![A-Za-z0-9-])")]
    private static partial Regex UuidTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9.-])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{5}\.\.\.(?![A-Za-z0-9.-])")]
    private static partial Regex TruncatedUuidTokenRegex();
}
