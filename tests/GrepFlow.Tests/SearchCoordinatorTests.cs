using System.Runtime.CompilerServices;
using GrepFlow.Interop;
using GrepFlow.Presentation;
using GrepFlow.Search;
using GrepFlow.Settings;
using Xunit;

namespace GrepFlow.Tests;

public sealed class SearchCoordinatorTests
{
    private const string Folder = @"C:\workspace";

    [Fact]
    public async Task ShortQueryStartsSubtitleWithFilePilotSource()
    {
        var coordinator = CreateCoordinator(
            new ActiveFolder(Folder, "FilePilot", FromNearestWindow: false),
            new EmptySearchEngine());

        var results = await coordinator.QueryAsync("ab", CancellationToken.None);

        Assert.Equal("FilePilot | Type at least 3 characters", results[0].SubTitle);
    }

    [Fact]
    public async Task LineCommentPatternCanSearchWithTwoCharacters()
    {
        var coordinator = CreateCoordinator(
            new ActiveFolder(Folder, "FilePilot", FromNearestWindow: false),
            new EmptySearchEngine());

        var results = await coordinator.QueryAsync("//", CancellationToken.None);

        var segments = results[0].SubTitle.Split(" | ");
        Assert.Equal("FilePilot", segments[0]);
        Assert.Equal("0 matches", segments[1]);
        Assert.EndsWith(" ms", segments[2]);
    }

    [Theory]
    [InlineData("Codex CLI")]
    [InlineData("Claude Code")]
    public async Task SuccessfulSearchStartsSubtitleWithTerminalAgentSource(string sourceName)
    {
        var coordinator = CreateCoordinator(
            new ActiveFolder(Folder, sourceName, FromNearestWindow: false),
            new EmptySearchEngine());

        var results = await coordinator.QueryAsync("abc", CancellationToken.None);

        var segments = results[0].SubTitle.Split(" | ");
        Assert.Equal(sourceName, segments[0]);
        Assert.Equal("0 matches", segments[1]);
        Assert.EndsWith(" ms", segments[2]);
    }

    [Fact]
    public async Task ReportedFailureStartsSubtitleWithSourceAndPreservesNearestWindowFallback()
    {
        var coordinator = CreateCoordinator(
            new ActiveFolder(Folder, "FilePilot", FromNearestWindow: true),
            new FailingSearchEngine("invalid pattern"));

        var results = await coordinator.QueryAsync("abc", CancellationToken.None);

        Assert.Equal(
            "FilePilot | Search failed: invalid pattern | nearest window",
            results[0].SubTitle);
    }

    [Fact]
    public async Task MissingWorkspaceDoesNotShowSourceName()
    {
        var coordinator = CreateCoordinator(null, new EmptySearchEngine());

        var results = await coordinator.QueryAsync("abc", CancellationToken.None);

        Assert.Equal("No workspace found", results[0].SubTitle);
    }

    [Theory]
    [InlineData("needle -- --pre powershell.exe", "Unsupported search option: --pre")]
    [InlineData("needle -- -g", "Search option requires a value: -g")]
    [InlineData(@"needle -- C:\Windows\win.ini", @"Search paths are not supported: C:\Windows\win.ini")]
    [InlineData("needle -- -g '*.cs", "Unterminated quote in search options")]
    public async Task InvalidOptionsReturnStatusWithoutInvokingEngine(string query, string expected)
    {
        var engine = new RecordingSearchEngine();
        var coordinator = CreateCoordinator(
            new ActiveFolder(Folder, "FilePilot", FromNearestWindow: false),
            engine);

        var results = await coordinator.QueryAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(expected, results[0].SubTitle);
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task SafeOptionsInvokeEngineWithTypedRequest()
    {
        var engine = new RecordingSearchEngine();
        var coordinator = CreateCoordinator(
            new ActiveFolder(Folder, "FilePilot", FromNearestWindow: false),
            engine);

        await coordinator.QueryAsync("needle -- -F -g *.cs --hidden", CancellationToken.None);

        Assert.Equal(1, engine.CallCount);
        Assert.NotNull(engine.Request);
        Assert.Equal("needle", engine.Request.Pattern);
        Assert.Equal(Folder, engine.Request.Folder);
        Assert.True(engine.Request.UserOptions.FixedStrings);
        Assert.True(engine.Request.UserOptions.IncludeHidden);
        Assert.Equal(new[] { "*.cs" }, engine.Request.UserOptions.Globs);
    }

    private static SearchCoordinator CreateCoordinator(ActiveFolder? folder, ISearchEngine engine)
    {
        var texts = new TestTextProvider();
        var resultFactory = new ResultFactory(
            new LineWindow(),
            null!,
            texts,
            null!,
            "app.png",
            "missing.png");

        return new SearchCoordinator(
            new StubActiveFolderProvider(folder),
            engine,
            new QueryTextParser(),
            resultFactory,
            texts,
            new RipgrepOptions(
                MaxResults: 20,
                MinPatternLength: 3,
                DebounceMilliseconds: 0),
            new RipgrepExecutable("rg.exe"),
            new HintPicker(new PluginSettings()),
            new PluginLog(Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"))));
    }

    private sealed class StubActiveFolderProvider : IActiveFolderProvider
    {
        private readonly ActiveFolder? _folder;

        public StubActiveFolderProvider(ActiveFolder? folder) => _folder = folder;

        public ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ActiveFolder?>(_folder);
        }
    }

    private sealed class EmptySearchEngine : ISearchEngine
    {
        public async IAsyncEnumerable<RipgrepMatch> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FailingSearchEngine : ISearchEngine
    {
        private readonly string _message;

        public FailingSearchEngine(string message) => _message = message;

        public async IAsyncEnumerable<RipgrepMatch> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            throw SearchFailure.Reported(_message);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class RecordingSearchEngine : ISearchEngine
    {
        public int CallCount { get; private set; }

        public SearchRequest? Request { get; private set; }

        public async IAsyncEnumerable<RipgrepMatch> SearchAsync(
            SearchRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            CallCount++;
            Request = request;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestTextProvider : ITextProvider
    {
        public string Get(string key, params object?[] arguments) => key switch
        {
            TextKeys.PluginGrepflowPluginName => "GrepFlow",
            TextKeys.PluginGrepflowTypeAtLeastCharacters => $"Type at least {arguments[0]} characters",
            TextKeys.PluginGrepflowExplorerNotFound => "No workspace found",
            TextKeys.PluginGrepflowMatches => $"{arguments[0]} matches",
            TextKeys.PluginGrepflowDuration => $"{arguments[0]} ms",
            TextKeys.PluginGrepflowNearestWindow => "nearest window",
            TextKeys.PluginGrepflowSubtitleSeparator => " | ",
            TextKeys.PluginGrepflowSearchFailed => $"Search failed: {arguments[0]}",
            TextKeys.PluginGrepflowUnsupportedSearchOption => $"Unsupported search option: {arguments[0]}",
            TextKeys.PluginGrepflowMissingSearchOptionValue => $"Search option requires a value: {arguments[0]}",
            TextKeys.PluginGrepflowUnexpectedSearchPath => $"Search paths are not supported: {arguments[0]}",
            TextKeys.PluginGrepflowUnterminatedSearchQuote => "Unterminated quote in search options",
            TextKeys.PluginGrepflowHintFixed => "use fixed strings",
            TextKeys.PluginGrepflowHintHidden => "include hidden files",
            _ => key,
        };
    }
}
