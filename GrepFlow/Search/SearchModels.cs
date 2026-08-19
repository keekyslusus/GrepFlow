namespace GrepFlow.Search;

public enum SearchCaseMode
{
    Smart,
    Ignore,
    Sensitive,
}

public sealed record RipgrepUserOptions(
    SearchCaseMode CaseMode,
    bool FixedStrings,
    bool WordRegexp,
    bool LineRegexp,
    bool IncludeHidden,
    bool IncludeIgnored,
    IReadOnlyList<string> Globs,
    IReadOnlyList<string> CaseInsensitiveGlobs,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> ExcludedTypes)
{
    public static RipgrepUserOptions Default { get; } = new(
        SearchCaseMode.Smart,
        FixedStrings: false,
        WordRegexp: false,
        LineRegexp: false,
        IncludeHidden: false,
        IncludeIgnored: false,
        Globs: Array.Empty<string>(),
        CaseInsensitiveGlobs: Array.Empty<string>(),
        Types: Array.Empty<string>(),
        ExcludedTypes: Array.Empty<string>());

    public bool HasAnyOption =>
        CaseMode != SearchCaseMode.Smart
        || FixedStrings
        || WordRegexp
        || LineRegexp
        || IncludeHidden
        || IncludeIgnored
        || Globs.Count > 0
        || CaseInsensitiveGlobs.Count > 0
        || Types.Count > 0
        || ExcludedTypes.Count > 0;
}

public sealed record SearchRequest(string Pattern, string Folder, RipgrepUserOptions UserOptions);

public sealed record RipgrepMatch(
    string AbsolutePath,
    string RelativePath,
    int LineNumber,
    string LineText,
    int MatchStart,
    int MatchLength);

// Executable path lives on RipgrepExecutable so install can refresh it in-process.
public sealed record RipgrepOptions(
    int MaxResults,
    int MinPatternLength,
    int DebounceMilliseconds);

public enum QueryParseErrorKind
{
    UnsupportedOption,
    MissingOptionValue,
    UnexpectedPositionalArgument,
    UnterminatedQuote,
}

public sealed record QueryParseError(QueryParseErrorKind Kind, string Token);

public sealed record ParsedQuery(
    string Pattern,
    RipgrepUserOptions UserOptions,
    QueryParseError? Error = null);

public static class SearchFailure
{
    private const string Key = "GrepFlow.ReportedByRipgrep";

    public static Exception Reported(string message)
    {
        var failure = new InvalidOperationException(message);
        failure.Data[Key] = true;
        return failure;
    }

    public static bool IsReported(Exception failure) => failure.Data.Contains(Key);
}
