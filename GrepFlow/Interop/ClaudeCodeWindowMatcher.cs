using System.IO;

namespace GrepFlow.Interop;

public sealed class ClaudeCodeWindowMatcher
{
    private const int MaxTextLength = 32 * 1024;
    private const int MaxHeaderLines = 20;

    private readonly string _userProfile;

    public ClaudeCodeWindowMatcher()
        : this(ResolveUserProfile())
    {
    }

    public ClaudeCodeWindowMatcher(string userProfile)
    {
        _userProfile = NormalizeForComparison(userProfile);
    }

    public string? Match(string? visibleText, IEnumerable<string> candidates)
    {
        var pathLines = ReadHeaderPathLines(visibleText);
        if (pathLines is null) return null;

        var normalizedCandidates = candidates
            .Select(path => new Candidate(path, NormalizeForComparison(path)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Normalized))
            .DistinctBy(candidate => candidate.Normalized, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var fullMatches = normalizedCandidates
            .Where(candidate => FullForms(candidate.Normalized).Any(form =>
                pathLines.Any(line => ContainsWholePath(line, form))))
            .ToArray();
        if (fullMatches.Length == 1) return fullMatches[0].Original;
        if (fullMatches.Length > 1) return null;

        var basenameMatches = normalizedCandidates
            .GroupBy(
                candidate => Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate.Normalized)),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1)
            .Select(group => group.Single())
            .Where(candidate => pathLines.Any(line => ContainsPathSegment(
                line,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate.Normalized)))))
            .ToArray();

        return basenameMatches.Length == 1 ? basenameMatches[0].Original : null;
    }

    public bool HasHeaderEvidence(string? visibleText)
        => ReadHeaderPathLines(visibleText) is not null;

    private static string[]? ReadHeaderPathLines(string? visibleText)
    {
        if (string.IsNullOrWhiteSpace(visibleText) ||
            visibleText.Length > MaxTextLength ||
            visibleText.IndexOf('\0') >= 0)
            return null;

        var headerLines = visibleText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Take(MaxHeaderLines)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (!headerLines.Any(line => line.Contains("Claude Code", StringComparison.OrdinalIgnoreCase)))
            return null;

        var pathLines = headerLines
            .Select(NormalizeSlashes)
            .Where(IsPathShaped)
            .ToArray();
        return pathLines.Length == 0 ? null : pathLines;
    }

    private IEnumerable<string> FullForms(string candidate)
    {
        yield return candidate;

        if (!string.IsNullOrWhiteSpace(_userProfile) &&
            candidate.StartsWith(_userProfile, StringComparison.OrdinalIgnoreCase) &&
            (candidate.Length == _userProfile.Length || candidate[_userProfile.Length] == '\\'))
        {
            var suffix = candidate[_userProfile.Length..].TrimStart('\\');
            yield return suffix.Length == 0 ? "~" : $"~\\{suffix}";
        }
    }

    private static bool ContainsWholePath(string line, string path)
    {
        var start = 0;
        while ((start = line.IndexOf(path, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = start + path.Length;
            var validBefore = start == 0 || !IsPathTokenCharacter(line[start - 1]);
            var validAfter = end == line.Length || !IsPathTokenCharacter(line[end]);
            if (validBefore && validAfter) return true;
            start++;
        }

        return false;
    }

    private static bool ContainsPathSegment(string line, string segment)
    {
        var start = 0;
        while ((start = line.IndexOf(segment, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = start + segment.Length;
            var separatorBefore = start > 0 && line[start - 1] == '\\';
            var validAfter = end == line.Length || !IsPathTokenCharacter(line[end]);
            if (separatorBefore && validAfter) return true;
            start++;
        }

        return false;
    }

    private static bool IsPathShaped(string line)
        => line.Contains("~\\", StringComparison.Ordinal) ||
           line.Contains(":\\", StringComparison.Ordinal) ||
           line.Contains("...\\", StringComparison.Ordinal) ||
           line.Contains("…\\", StringComparison.Ordinal);

    private static bool IsPathTokenCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '\\' or '/' or '_' or '-' or '.';

    private static string NormalizeForComparison(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return NormalizeSlashes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeSlashes(string value) => value.Replace('/', '\\');

    private static string ResolveUserProfile()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        return string.IsNullOrWhiteSpace(profile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : profile;
    }

    private sealed record Candidate(string Original, string Normalized);
}
