using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class CodexCodeWindowMatcherTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-CodexMatcher-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void ProductMarkerAndUniqueTitleMatchSelectCandidate()
    {
        const string folder = @"C:\repo";

        Assert.Equal(folder, new CodexCodeWindowMatcher().Match(
            "OpenAI Codex\nworking",
            [folder]));
    }

    [Fact]
    public void MatchingDirectoryWithoutProductMarkerIsRejected()
    {
        Assert.Null(new CodexCodeWindowMatcher().Match(
            "Command Prompt\nC:\\repo>",
            [@"C:\repo"]));
    }

    [Fact]
    public void UniqueFooterDirectoryDoesNotRequireProductMarker()
    {
        var folder = CreateFolder("footer-match");

        Assert.Equal(
            folder,
            new CodexCodeWindowMatcher().Match($"gpt-5.6-sol high · {folder}", [folder]),
            ignoreCase: true);
    }

    [Fact]
    public void ProductMarkerDoesNotResolveAmbiguousTitleCandidates()
    {
        Assert.Null(new CodexCodeWindowMatcher().Match(
            "OpenAI Codex",
            [@"C:\one\repo", @"C:\two\repo"]));
    }

    [Fact]
    public void ProductNameInConversationOutsideHeaderIsRejected()
    {
        var text = string.Join(
            '\n',
            Enumerable.Range(1, 20).Select(index => $"line {index}").Append("OpenAI Codex"));

        Assert.False(new CodexCodeWindowMatcher().HasProductMarker(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0OpenAI Codex")]
    public void MalformedOrEmptyTextIsRejected(string? text)
    {
        Assert.False(new CodexCodeWindowMatcher().HasProductMarker(text));
    }

    [Fact]
    public void OversizedTextIsRejected()
    {
        Assert.False(new CodexCodeWindowMatcher().HasProductMarker(
            "OpenAI Codex" + new string('x', 33 * 1024)));
    }

    [Fact]
    public void AbsoluteFooterPathSelectsCandidateWithoutWelcomeMarker()
    {
        var folder = CreateFolder("repo");

        var evidence = Analyze($"conversation\ngpt-5.6-sol high · {folder}", "working | repo", [folder]);

        Assert.False(evidence.HasProductMarker);
        Assert.Equal(folder, evidence.VisibleWorkingDirectory, ignoreCase: true);
        Assert.Equal([folder], evidence.TitleMatches, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FooterPathHintCanPrecedeOtherStatusLineItems()
    {
        var launch = CreateFolder("status-order-launch");
        var resumed = CreateFolder("status order resumed");

        var evidence = Analyze(
            $"conversation\n{resumed} \u00b7 gpt-5.6-sol high",
            "Command Prompt",
            [launch]);

        Assert.Null(evidence.VisibleWorkingDirectory);
        Assert.Equal(resumed, evidence.VisiblePathHint, ignoreCase: true);
    }

    [Fact]
    public void FooterPathMayContainMiddleDot()
    {
        var folder = CreateFolder("repo\u00b7name");

        var evidence = Analyze($"gpt-5.6-sol high \u00b7 {folder}", "repo\u00b7name", [folder]);

        Assert.Equal(folder, evidence.VisiblePathHint, ignoreCase: true);
    }

    [Fact]
    public void LowestStatusLinePathWinsOverEarlierConversationPath()
    {
        var launch = CreateFolder("conversation-launch");
        var resumed = CreateFolder("footer-resumed");

        var evidence = Analyze(
            $"assistant: old file is in {launch}\n{resumed} \u00b7 gpt-5.6-sol high",
            "Command Prompt",
            [launch, resumed]);

        Assert.Equal(resumed, evidence.VisibleWorkingDirectory, ignoreCase: true);
        Assert.Equal(resumed, evidence.VisiblePathHint, ignoreCase: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrSingleItemStatusLineDoesNotPromoteConversationPath(bool hasSingleItemStatusLine)
    {
        var foreign = CreateFolder("foreign-session");
        var text = $"assistant \u00b7 {foreign}";
        if (hasSingleItemStatusLine) text += "\ngpt-5.6-sol high";

        var evidence = Analyze(
            text,
            "Command Prompt",
            [foreign]);

        Assert.Null(evidence.VisibleWorkingDirectory);
        Assert.Null(evidence.VisiblePathHint);
    }

    [Fact]
    public void DisabledStatusLineDoesNotPromoteSentenceEndingInPath()
    {
        var foreign = CreateFolder("sentence-foreign");

        var evidence = Analyze(
            $"I checked the other project at {foreign}",
            "Command Prompt",
            [foreign]);

        Assert.Null(evidence.VisibleWorkingDirectory);
        Assert.Null(evidence.VisiblePathHint);
    }

    [Fact]
    public void SingleCurrentDirectoryStatusItemRemainsUsable()
    {
        var folder = CreateFolder("single-current-dir");

        var evidence = Analyze(folder, "Command Prompt", [folder]);

        Assert.Equal(folder, evidence.VisibleWorkingDirectory, ignoreCase: true);
        Assert.Equal(folder, evidence.VisiblePathHint, ignoreCase: true);
    }

    [Fact]
    public void HomeRelativeFooterPathMatchesUserProfile()
    {
        var folder = CreateFolder(Path.Combine("Desktop", "vm", "vibeclown"));

        var evidence = new CodexCodeWindowMatcher().Analyze(
            "gpt-5.6-sol · ~\\Desktop\\vm\\vibeclown",
            "vibeclown",
            [folder],
            [],
            _temporaryDirectory);

        Assert.Equal(folder, evidence.VisibleWorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void ConversationPathOutsideFooterRegionIsIgnored()
    {
        var folder = CreateFolder("repo");
        var text = string.Join('\n', new[] { $"mentioned {folder}" }.Concat(
            Enumerable.Range(0, 12).Select(index => $"later line {index}")));

        Assert.Null(Analyze(text, "repo", [folder]).VisibleWorkingDirectory);
    }

    [Fact]
    public void PathBoundariesRejectPrefixCollision()
    {
        var repo = CreateFolder("repo");
        var repository = CreateFolder("repository");

        var evidence = Analyze($"gpt-5.6-sol high · {repository}", "repository", [repo, repository]);

        Assert.Equal(repository, evidence.VisibleWorkingDirectory, ignoreCase: true);
    }

    [Theory]
    [InlineData("repo-old")]
    [InlineData("repo_v2")]
    [InlineData("repo.dev")]
    [InlineData("my repo")]
    public void ProjectAliasDoesNotMatchPrefixOfSameTitleSegment(string longerName)
    {
        var repo = CreateFolder("repo");
        var longer = CreateFolder(longerName);
        var matches = new CodexCodeWindowMatcher().FindTitleMatches(
            $"Working | {longerName}",
            [repo, longer],
            includeProjectAliases: false);

        Assert.Equal([longer], matches, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("⠋ vibeclown")]
    [InlineData("vibeclown ⠹")]
    [InlineData("vibeclown ⠋ Working")]
    [InlineData("Working ⠋ vibeclown")]
    [InlineData("[ ! ] Action Required | vibeclown")]
    [InlineData("[ . ] Action Required | vibeclown")]
    public void ProjectAliasMatchesKnownActivityAndActionRequiredTitles(string title)
    {
        var folder = CreateFolder("vibeclown");

        Assert.True(new CodexCodeWindowMatcher().TitleMatchesWorkingDirectory(
            title,
            folder,
            includeProjectAliases: false));
    }

    [Fact]
    public void TitleUuidMatchesOnlyExactActiveSession()
    {
        var active = Guid.NewGuid().ToString();
        var inactive = Guid.NewGuid().ToString();

        Assert.Equal(active, Analyze(null, $"repo | {active}", [], [active]).ThreadId);
        Assert.Null(Analyze(null, $"repo | {inactive}", [], [active]).ThreadId);
        Assert.Null(Analyze(null, $"x{active}y", [], [active]).ThreadId);
    }

    [Fact]
    public void ActualCodexTruncatedUuidTitleMatchesUniqueActiveSession()
    {
        const string active = "12345678-1234-1234-1234-123456789abc";
        var titleValue = CodexCodeWindowMatcher.ThreadTitleValue(active);

        Assert.Equal("12345678-1234-1234-1234-12345...", titleValue);
        Assert.Equal(active, Analyze(null, $"codex | repo | {titleValue}", [], [active]).ThreadId);
    }

    [Fact]
    public void TruncatedUuidPrefixMustIdentifyOneActiveSession()
    {
        const string first = "12345678-1234-1234-1234-123456789abc";
        const string second = "12345678-1234-1234-1234-12345abcdef0";
        var titleValue = CodexCodeWindowMatcher.ThreadTitleValue(first);

        Assert.Null(Analyze(null, titleValue, [], [first, second]).ThreadId);
    }

    [Fact]
    public void GitProjectRootAliasMatchesNestedWorkingDirectory()
    {
        var root = CreateFolder("repo-root");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(root, "src", "feature")).FullName;

        Assert.Contains(
            nested,
            new CodexCodeWindowMatcher().FindTitleMatches(
                "working | repo-root | app",
                [nested],
                includeProjectAliases: true),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitWorktreeMarkerFileDefinesProjectRootAlias()
    {
        var root = CreateFolder("worktree-root");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere");
        var nested = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;

        Assert.True(new CodexCodeWindowMatcher().TitleMatchesWorkingDirectory(
            "status | worktree-root",
            nested,
            includeProjectAliases: true));
    }

    [Fact]
    public void CodexProjectConfigDefinesAliasWhenNoGitRootExists()
    {
        var root = CreateFolder("configured-root");
        var config = Directory.CreateDirectory(Path.Combine(root, ".codex")).FullName;
        File.WriteAllText(Path.Combine(config, "config.toml"), "");
        var nested = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;

        Assert.True(new CodexCodeWindowMatcher().TitleMatchesWorkingDirectory(
            "configured-root | Codex",
            nested,
            includeProjectAliases: true));
    }

    [Fact]
    public void ProjectAliasParentWalkIsOptInAndCached()
    {
        var root = CreateFolder("cached-root");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var matcher = new CodexCodeWindowMatcher();

        Assert.Empty(matcher.FindTitleMatches("cached-root", [nested], includeProjectAliases: false));
        Assert.Equal(0, matcher.ProjectAliasProbeCount);
        Assert.Contains(
            nested,
            matcher.FindTitleMatches("cached-root", [nested], includeProjectAliases: true),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, matcher.ProjectAliasProbeCount);

        matcher.FindTitleMatches("cached-root", [nested], includeProjectAliases: true);

        Assert.Equal(1, matcher.ProjectAliasProbeCount);
    }

    [Fact]
    public void LongUnicodeNamesUseGraphemeCompatibleTruncation()
    {
        var name = string.Concat(Enumerable.Repeat("😀", 22)) + "abcdef";
        var truncated = CodexCodeWindowMatcher.TruncateProjectName(name);

        Assert.EndsWith("...", truncated);
        Assert.Equal(24, new System.Globalization.StringInfo(truncated).LengthInTextElements);
    }

    [Fact]
    public void ReorderedTitleSegmentsExposeProjectAndThreadId()
    {
        var folder = CreateFolder("reordered");
        var sessionId = Guid.NewGuid().ToString();

        var evidence = Analyze(
            $"gpt-5.6-sol high · {folder}",
            $"{sessionId} | Codex | reordered",
            [folder],
            [sessionId]);

        Assert.Equal(sessionId, evidence.ThreadId);
        Assert.Contains(folder, evidence.TitleMatches, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\0status")]
    public void InvalidUiTextDoesNotProduceFooterEvidence(string text)
    {
        var folder = CreateFolder("invalid-ui");

        Assert.Null(Analyze(text, "invalid-ui", [folder]).VisibleWorkingDirectory);
        Assert.Null(Analyze(new string('x', 33 * 1024), "invalid-ui", [folder]).VisibleWorkingDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private CodexWindowEvidence Analyze(
        string? text,
        string title,
        IEnumerable<string> candidates,
        IEnumerable<string>? sessions = null)
        => new CodexCodeWindowMatcher().Analyze(
            text,
            title,
            candidates,
            sessions ?? [],
            _temporaryDirectory);

    private string CreateFolder(string name)
        => Directory.CreateDirectory(Path.Combine(_temporaryDirectory, name)).FullName;
}
