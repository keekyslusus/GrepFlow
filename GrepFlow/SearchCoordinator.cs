using System.Diagnostics;
using Flow.Launcher.Plugin;
using GrepFlow.Interop;
using GrepFlow.Presentation;
using GrepFlow.Search;

namespace GrepFlow;

public sealed class SearchCoordinator
{
    private readonly IActiveFolderProvider _folders;
    private readonly ISearchEngine _engine;
    private readonly QueryTextParser _queryParser;
    private readonly ResultFactory _results;
    private readonly ITextProvider _texts;
    private readonly RipgrepOptions _options;
    private readonly RipgrepExecutable _executable;
    private readonly HintPicker _hints;
    private readonly PluginLog _log;

    public SearchCoordinator(
        IActiveFolderProvider folders,
        ISearchEngine engine,
        QueryTextParser queryParser,
        ResultFactory results,
        ITextProvider texts,
        RipgrepOptions options,
        RipgrepExecutable executable,
        HintPicker hints,
        PluginLog log)
    {
        _folders = folders;
        _engine = engine;
        _queryParser = queryParser;
        _results = results;
        _texts = texts;
        _options = options;
        _executable = executable;
        _hints = hints;
        _log = log;
    }

    public async Task<List<Result>> QueryAsync(string search, CancellationToken token)
    {
        try
        {
            return await RunAsync(search, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new List<Result>();
        }
        catch (Exception exception) when (SearchFailure.IsReported(exception))
        {
            // typing "parseConfig(x)" passes through "parseConfig(", which ripgrep rejects. Expected
            // input, so one log line rather than an exception dump. Prefer handling inside RunAsync
            // (folder + nearest XOR hint); this is a fallback when failure escapes that path.
            _log.Warn(nameof(SearchCoordinator), $"ripgrep rejected the query: {exception.Message}");
            var failure = _texts.Get(TextKeys.PluginGrepflowSearchFailed, exception.Message);
            var hint = _hints.Pick(
                new HintContext(
                    MatchCount: 0,
                    LimitReached: false,
                    FromNearestWindow: false,
                    UserOptions: RipgrepUserOptions.Default,
                    Pattern: string.Empty,
                    RipgrepReportedFailure: true),
                _texts);
            return Single(Status(Name(), StatusSubtitle.Join(_texts, failure, hint), null));
        }
        catch (Exception exception)
        {
            _log.Error(nameof(SearchCoordinator), "query failed", exception);
            return Single(Status(Name(), _texts.Get(TextKeys.PluginGrepflowSearchFailed, exception.Message), null));
        }
    }

    private async Task<List<Result>> RunAsync(string search, CancellationToken token)
    {
        await Task.Delay(_options.DebounceMilliseconds, token).ConfigureAwait(false);

        if (!_executable.IsAvailable)
            return Single(_results.CreateInstallRipgrepResult());

        var active = await _folders.GetActiveFolderAsync(token).ConfigureAwait(false);
        if (active is null) return Single(Status(Name(), _texts.Get(TextKeys.PluginGrepflowExplorerNotFound), null));

        var folder = active.Path;

        var parsed = _queryParser.Parse(search);
        if (parsed.Error is not null)
            return Single(Status(folder, Describe(parsed.Error), folder));

        if (parsed.Pattern.Length < _options.MinPatternLength
            && parsed.Pattern is not "//")
            return Single(Status(
                folder,
                StatusSubtitle.Join(
                    _texts,
                    active.SourceName,
                    _texts.Get(TextKeys.PluginGrepflowTypeAtLeastCharacters, _options.MinPatternLength),
                    NearestWindowLabel(active.FromNearestWindow)),
                folder));

        var request = new SearchRequest(parsed.Pattern, folder, parsed.UserOptions);
        var stopwatch = Stopwatch.StartNew();
        var matches = new List<Result>(_options.MaxResults);
        var limitReached = false;

        try
        {
            await foreach (var match in _engine.SearchAsync(request, token).ConfigureAwait(false))
            {
                if (matches.Count >= _options.MaxResults)
                {
                    // leaving the loop disposes the enumerator, which kills ripgrep
                    limitReached = true;
                    break;
                }

                matches.Add(_results.CreateMatch(match, _options.MaxResults - matches.Count));
            }
        }
        catch (Exception exception) when (SearchFailure.IsReported(exception))
        {
            stopwatch.Stop();
            _log.Warn(nameof(SearchCoordinator), $"ripgrep rejected the query: {exception.Message}");
            return Single(BuildFailureStatus(active, parsed, exception.Message));
        }

        stopwatch.Stop();
        token.ThrowIfCancellationRequested();

        var nearest = NearestWindowLabel(active.FromNearestWindow);
        // HINT XOR NEAREST: pick only when nearest is absent (see StatusSubtitle).
        var hint = nearest is null
            ? _hints.Pick(
                new HintContext(
                    MatchCount: matches.Count,
                    LimitReached: limitReached,
                    FromNearestWindow: active.FromNearestWindow,
                    UserOptions: parsed.UserOptions,
                    Pattern: parsed.Pattern,
                    RipgrepReportedFailure: false),
                _texts)
            : null;

        var results = new List<Result>(matches.Count + 2)
        {
            Status(
                folder,
                StatusSubtitle.Join(
                    _texts,
                    active.SourceName,
                    _texts.Get(TextKeys.PluginGrepflowMatches, matches.Count),
                    _texts.Get(TextKeys.PluginGrepflowDuration, stopwatch.ElapsedMilliseconds),
                    nearest,
                    hint),
                folder,
                noMatches: matches.Count == 0),
        };
        results.AddRange(matches);
        if (limitReached) results.Add(_results.CreateLimitNotice(_options.MaxResults));
        return results;
    }

    private Result BuildFailureStatus(
        ActiveFolder active,
        ParsedQuery parsed,
        string message)
    {
        var folder = active.Path;
        var fromNearestWindow = active.FromNearestWindow;
        var nearest = NearestWindowLabel(fromNearestWindow);
        var hint = nearest is null
            ? _hints.Pick(
                new HintContext(
                    MatchCount: 0,
                    LimitReached: false,
                    FromNearestWindow: fromNearestWindow,
                    UserOptions: parsed.UserOptions,
                    Pattern: parsed.Pattern,
                    RipgrepReportedFailure: true),
                _texts)
            : null;

        return Status(
            folder,
            StatusSubtitle.Join(
                _texts,
                active.SourceName,
                _texts.Get(TextKeys.PluginGrepflowSearchFailed, message),
                nearest,
                hint),
            folder);
    }

    private Result Status(string title, string subTitle, string? folder, bool noMatches = false)
        => _results.CreateStatus(title, subTitle, folder, noMatches);

    private string Name() => _texts.Get(TextKeys.PluginGrepflowPluginName);

    private string Describe(QueryParseError error) => error.Kind switch
    {
        QueryParseErrorKind.UnsupportedOption =>
            _texts.Get(TextKeys.PluginGrepflowUnsupportedSearchOption, error.Token),
        QueryParseErrorKind.MissingOptionValue =>
            _texts.Get(TextKeys.PluginGrepflowMissingSearchOptionValue, error.Token),
        QueryParseErrorKind.UnexpectedPositionalArgument =>
            _texts.Get(TextKeys.PluginGrepflowUnexpectedSearchPath, error.Token),
        QueryParseErrorKind.UnterminatedQuote =>
            _texts.Get(TextKeys.PluginGrepflowUnterminatedSearchQuote),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error.Kind, null),
    };

    private string? NearestWindowLabel(bool fromNearestWindow)
        => fromNearestWindow ? _texts.Get(TextKeys.PluginGrepflowNearestWindow) : null;

    private static List<Result> Single(Result result) => new(1) { result };
}
