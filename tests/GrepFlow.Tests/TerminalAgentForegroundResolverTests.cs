using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class TerminalAgentForegroundResolverTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-AgentResolver-{Guid.NewGuid():N}");

    public TerminalAgentForegroundResolverTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void DedicatedShellResolvesUniqueCodexCwdWithoutTitleMatch()
    {
        const string image = "cmd.exe";
        const string title = "Command Prompt";
        var folder = CreateFolder("project");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, image, title),
            Snapshot = Snapshot(Process(10, 1, image), Process(20, 10, "codex.exe")),
        };
        fixture.WorkingDirectories[20] = folder;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(1, fixture.AccessibleTextReads);
    }

    [Fact]
    public void DedicatedShellResumeUsesActiveSessionCwd()
    {
        const string image = "cmd.exe";
        var launch = CreateFolder($"{image}-launch");
        var resumed = CreateFolder($"{image}-resumed");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, image, Path.GetFileName(resumed)),
            Snapshot = Snapshot(
                Process(10, 1, image),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(
                    Guid.NewGuid().ToString(),
                    resumed,
                    DateTime.UtcNow,
                    launch),
            ],
        };
        fixture.WorkingDirectories[20] = launch;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);
        Assert.Equal(1, fixture.CodexSessionReads);
        Assert.Equal(1, fixture.AccessibleTextReads);
    }

    [Fact]
    public void DedicatedResumeCorrelatesInitialCwdWithSecondActiveSession()
    {
        var launch = CreateFolder("dedicated-launch");
        var resumedInitial = CreateFolder("resumed-original-launch");
        var resumed = CreateFolder("dedicated-resumed");
        var otherInitial = CreateFolder("other-launch");
        var otherCurrent = CreateFolder("other-current");
        var sessionId = Guid.NewGuid().ToString();
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "dedicated-resumed"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(sessionId, resumed, DateTime.UtcNow, resumedInitial),
                new CodexCliSession(Guid.NewGuid().ToString(), otherCurrent, DateTime.UtcNow, otherInitial),
            ],
        };
        fixture.WorkingDirectories[20] = launch;
        var resolver = fixture.Resolver;

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);
        Assert.Equal(1, fixture.CodexSessionReads);

        fixture.CodexSessions =
        [
            new CodexCliSession(sessionId, otherCurrent, DateTime.UtcNow, resumedInitial),
        ];

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, otherCurrent);
        Assert.Equal(1, fixture.CodexSessionReads);
        Assert.Equal(1, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public void DedicatedShellDoesNotAdoptUncorrelatedGlobalSession()
    {
        var launch = CreateFolder("current-launch");
        var foreignInitial = CreateFolder("foreign-launch");
        var foreignCurrent = CreateFolder("foreign-current");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(
                    Guid.NewGuid().ToString(),
                    foreignCurrent,
                    DateTime.UtcNow,
                    foreignInitial),
            ],
        };
        fixture.WorkingDirectories[20] = launch;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, launch);
        Assert.Equal(0, fixture.CodexSessionReads);
    }

    [Fact]
    public void DedicatedResumeBetweenSiblingGitDirectoriesUsesVisibleFooter()
    {
        var root = CreateFolder("dedicated-repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var launch = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(root, "tests")).FullName;
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "dedicated-repo"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), resumed, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ => $"conversation\nmodel high · {resumed}",
        };
        fixture.WorkingDirectories[20] = launch;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);
        Assert.Equal(1, fixture.AccessibleTextReads);
        Assert.Equal(1, fixture.CodexSessionReads);
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("OpenAI Codex")]
    public void DedicatedCustomCodexTitleUsesVisibleFooterAfterResume(string title)
    {
        var launch = CreateFolder("custom-title-launch");
        var resumed = CreateFolder("custom-title-resumed");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", title),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), resumed, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ => $"conversation\ncurrent-dir · {resumed}",
        };
        fixture.WorkingDirectories[20] = launch;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);
    }

    [Fact]
    public async Task DedicatedDisabledTerminalTitleUsesCurrentDirectoryFooter()
    {
        const string title = "Command Prompt";
        var launch = CreateFolder("disabled-title-launch");
        var resumed = CreateFolder("disabled-title-resumed");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", title),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), resumed, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ => $"conversation\ncurrent-dir · {resumed}",
        };
        fixture.WorkingDirectories[20] = launch;
        var source = new TerminalAgentWorkspaceSource(fixture.Resolver);

        Assert.True(source.MatchesForeground(new IntPtr(1)));
        Assert.Equal(
            resumed,
            (await source.GetActiveFolderAsync(CancellationToken.None))?.Path,
            ignoreCase: true);
        Assert.Equal(1, fixture.AccessibleTextReads);
        Assert.Equal(1, fixture.CodexSessionReads);
    }

    [Fact]
    public void DedicatedGenericTitleUsesCurrentDirectoryBeforeLaterStatusItems()
    {
        var launch = CreateFolder("status-order-launch");
        var resumed = CreateFolder("status-order-resumed");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), resumed, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ =>
                $"assistant: old file is in {launch}\n{resumed} \u00b7 gpt-5.6-sol high",
        };
        fixture.WorkingDirectories[20] = launch;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);
        Assert.Equal(1, fixture.CodexSessionReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DedicatedGenericTitleIgnoresConversationPathWithoutUsableStatusLine(
        bool hasSingleItemStatusLine)
    {
        var live = CreateFolder("pathless-status-live");
        var foreign = CreateFolder("pathless-status-foreign");
        var visibleText = $"assistant \u00b7 {foreign}";
        if (hasSingleItemStatusLine) visibleText += "\ngpt-5.6-sol high";
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), foreign, DateTime.UtcNow),
            ],
            AccessibleText = _ => visibleText,
        };
        fixture.WorkingDirectories[20] = live;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, live);
        Assert.Equal(0, fixture.CodexSessionReads);
    }

    [Fact]
    public void DedicatedGenericTitleIgnoresSentenceEndingInForeignPathWithoutStatusLine()
    {
        var live = CreateFolder("sentence-live");
        var foreign = CreateFolder("sentence-foreign");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), foreign, DateTime.UtcNow),
            ],
            AccessibleText = _ => $"I checked the other project at {foreign}",
        };
        fixture.WorkingDirectories[20] = live;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, live);
        Assert.Equal(0, fixture.CodexSessionReads);
    }

    [Theory]
    [InlineData("Command Prompt")]
    [InlineData("project")]
    public void ConfirmedDedicatedWindowPrefersFreshFooterAfterSessionChange(string title)
    {
        var launch = CreateFolder("session-change-launch");
        var first = CreateFolder(Path.Combine("first-session", "project"));
        var second = CreateFolder(Path.Combine("second-session", "project"));
        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();
        var visible = first;
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", title),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe"),
                Process(30, 99, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(firstId, first, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ =>
                $"assistant: old file is in {first}\n{visible} \u00b7 gpt-5.6-sol high",
        };
        fixture.WorkingDirectories[20] = launch;
        var resolver = fixture.Resolver;

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, first);

        visible = second;
        fixture.CodexSessions =
        [
            new CodexCliSession(firstId, first, DateTime.UtcNow, launch),
            new CodexCliSession(secondId, second, DateTime.UtcNow, launch),
        ];

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, second);
        Assert.Equal(2, fixture.CodexSessionReads);
        Assert.Equal(1, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public void ConfirmedDedicatedWindowSurvivesIdleToActivityTitleChange()
    {
        var launch = CreateFolder("activity-title-launch");
        var resumed = CreateFolder("vibeclown");
        var sessionId = Guid.NewGuid().ToString();
        var title = "vibeclown";
        var showFooter = true;
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", title),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(sessionId, resumed, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ => showFooter
                ? $"conversation\n{resumed} \u00b7 gpt-5.6-sol high"
                : "conversation",
        };
        fixture.WorkingDirectories[20] = launch;
        var resolver = fixture.Resolver;

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);

        title = "⠋ vibeclown";
        showFooter = false;

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);
        Assert.Equal(1, fixture.CodexSessionReads);
        Assert.Equal(1, fixture.CodexTargetedSessionReads);
    }

    [Theory]
    [InlineData("vibeclown ⠋ Working")]
    [InlineData("Working ⠋ vibeclown")]
    public void DedicatedCustomActivityOrderingSelectsProject(string title)
    {
        var launch = CreateFolder("custom-activity-launch");
        var project = CreateFolder("vibeclown");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", title),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), project, DateTime.UtcNow),
            ],
            AccessibleText = _ => "conversation",
        };
        fixture.WorkingDirectories[20] = launch;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, project);
    }

    [Theory]
    [InlineData("repo-old")]
    [InlineData("repo_v2")]
    [InlineData("repo.dev")]
    [InlineData("my repo")]
    public void DedicatedTitleSelectsExactProjectSegment(string selectedName)
    {
        var launch = CreateFolder("title-boundary-launch");
        var repo = CreateFolder("repo");
        var selected = CreateFolder(selectedName);
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", $"Working | {selectedName}"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), repo, DateTime.UtcNow),
                new CodexCliSession(Guid.NewGuid().ToString(), selected, DateTime.UtcNow),
            ],
            AccessibleText = _ => "conversation",
        };
        fixture.WorkingDirectories[20] = launch;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, selected);
    }

    [Fact]
    public void SharedEffectiveCwdDoesNotCacheSessionByInitialCwd()
    {
        var launch = CreateFolder("identity-launch");
        var actualInitial = CreateFolder("actual-initial");
        var shared = CreateFolder("shared-effective");
        var moved = CreateFolder("foreign-moved");
        var actualId = Guid.NewGuid().ToString();
        var foreignId = Guid.NewGuid().ToString();
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "shared-effective"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(actualId, shared, DateTime.UtcNow, actualInitial),
                new CodexCliSession(foreignId, shared, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ => $"conversation\ngpt-5.6-sol high · {shared}",
        };
        fixture.WorkingDirectories[20] = launch;
        var resolver = fixture.Resolver;

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, shared);
        Assert.Equal(0, fixture.CodexTargetedSessionReads);

        fixture.CodexSessions =
        [
            new CodexCliSession(actualId, shared, DateTime.UtcNow, actualInitial),
            new CodexCliSession(foreignId, moved, DateTime.UtcNow, launch),
        ];

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, shared);
        Assert.Equal(2, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public void ValidatedConhostResolvesUniqueCodexCwdWithoutTitleMatch()
    {
        var folder = CreateFolder("project");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(20, "conhost.exe", "Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "conhost.exe"),
                Process(30, 10, "codex.exe")),
        };
        fixture.WorkingDirectories[30] = folder;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
    }

    [Fact]
    public void DedicatedShellWithoutAgentDoesNotReadExpensiveState()
    {
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "Command Prompt"),
            Snapshot = Snapshot(Process(10, 1, "cmd.exe")),
        };

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(0, fixture.CwdReads);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);
        Assert.Equal(0, fixture.AccessibleTextReads);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void PowerShellAgentIsRejectedWithoutReadingExpensiveState(string shellImage)
    {
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, shellImage, "Codex"),
            Snapshot = Snapshot(
                Process(10, 1, shellImage),
                Process(20, 10, "codex.exe")),
        };

        Assert.False(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(0, fixture.CwdReads);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);
        Assert.Equal(0, fixture.AccessibleTextReads);
    }

    [Fact]
    public void CheapDedicatedMatchDoesNotReadCwdSessionsOrUia()
    {
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe")),
        };

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(0, fixture.CwdReads);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);
        Assert.Equal(0, fixture.AccessibleTextReads);
    }

    [Fact]
    public void CheapClaudeWindowsTerminalMatchUsesLiveCwdAndHeaderEvidence()
    {
        var folder = CreateFolder("claude-project");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("status Claude Code"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "claude.exe")),
            AccessibleText = _ => $"Claude Code\n{folder}",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(1, fixture.CwdReads);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);
        Assert.Equal(1, fixture.AccessibleTextReads);
    }

    [Fact]
    public void CheapNormalWindowsTerminalMatchDoesNotReadSessions()
    {
        var folder = CreateFolder("codex-project");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe"),
                Process(30, 10, "claude.exe")),
            AccessibleText = _ => "Command Prompt\nC:\\>",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.False(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(2, fixture.CwdReads);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);
        Assert.Equal(1, fixture.AccessibleTextReads);
    }

    [Fact]
    public void CheapCodexWindowsTerminalMatchUsesUniqueLiveCwdTitle()
    {
        var folder = CreateFolder("codex-project");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("status | codex-project"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(1, fixture.CwdReads);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(1, fixture.AccessibleTextReads);
    }

    [Fact]
    public void DedicatedShellWithSeveralCodexPathsUsesUniqueTitleEvidence()
    {
        var first = CreateFolder("first");
        var second = CreateFolder("second");
        var fixture = DedicatedCodexFixture("agent | first", first, second);

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, first);
    }

    [Fact]
    public void DedicatedShellWithSeveralPathsAndGenericTitleIsAmbiguous()
    {
        var fixture = DedicatedCodexFixture(
            "Command Prompt",
            CreateFolder("first"),
            CreateFolder("second"));

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public void WindowsTerminalResolvesCodexAndClaudeByExactHwndEvidence()
    {
        var codex = CreateFolder("codex-project");
        var claude = CreateFolder("claude-project");
        var fixture = SharedWindowsTerminalFixture(codex, claude);
        fixture.Inspect = window => window == new IntPtr(1)
            ? new TerminalWindow(10, "WindowsTerminal.exe", "status | codex-project")
            : new TerminalWindow(10, "WindowsTerminal.exe", "Claude Code");
        fixture.AccessibleText = window => window == new IntPtr(2)
            ? $"Claude Code\n{claude}"
            : "OpenAI Codex";

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, codex);
        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(2)), TerminalAgentKind.ClaudeCode, claude);
    }

    [Fact]
    public void NormalWindowsTerminalShellDoesNotClaimAgentFromSharedProcess()
    {
        var fixture = SharedWindowsTerminalFixture(
            CreateFolder("codex-project"),
            CreateFolder("claude-project"));
        fixture.Inspect = _ => new TerminalWindow(10, "WindowsTerminal.exe", "Command Prompt");
        fixture.AccessibleText = _ => "Command Prompt\nC:\\>";

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(3)));
    }

    [Fact]
    public void NormalWindowsTerminalShellInCodexDirectoryIsNotCodex()
    {
        var folder = CreateFolder("repo");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("Command Prompt repo"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
            ],
            AccessibleText = _ => $"Command Prompt\n{folder}>",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.False(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(0, fixture.CodexSessionReads);
    }

    [Fact]
    public void CodexWindowInSameDirectoryRequiresAndUsesProductMarker()
    {
        var folder = CreateFolder("repo");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("status | repo"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            AccessibleText = _ => "OpenAI Codex\nready",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
    }

    [Fact]
    public void TwoClaudeWindowsWithSameTitleResolveDifferentHeaders()
    {
        var first = CreateFolder("first");
        var second = CreateFolder("second");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "WindowsTerminal.exe", "Claude Code"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "claude.exe"),
                Process(30, 10, "claude.exe")),
            AccessibleText = window => $"Claude Code\n{(window == new IntPtr(1) ? first : second)}",
        };
        fixture.WorkingDirectories[20] = first;
        fixture.WorkingDirectories[30] = second;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, first);
        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(2)), TerminalAgentKind.ClaudeCode, second);
    }

    [Fact]
    public void StrongClaudeWindowMatchSkipsCodexSessionFallback()
    {
        var codex = CreateFolder("codex-project");
        var claude = CreateFolder("claude-project");
        var fixture = SharedWindowsTerminalFixture(codex, claude);
        fixture.Inspect = _ => WindowsTerminal("⠹ GrepFlow");
        fixture.AccessibleText = _ => $"Claude Code\n{claude}";

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, claude);
        Assert.Equal(0, fixture.CodexSessionReads);
    }

    [Fact]
    public void SeveralClaudeProcessesInSameDirectoryDeduplicate()
    {
        var folder = CreateFolder("project");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("Claude Code"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "claude.exe"),
                Process(30, 10, "claude.exe")),
            AccessibleText = _ => $"Claude Code\n{folder}",
        };
        fixture.WorkingDirectories[20] = folder;
        fixture.WorkingDirectories[30] = folder;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, folder);
    }

    [Fact]
    public void LiveClaudeCwdWinsOverStalePointerForSamePid()
    {
        var live = CreateFolder("live");
        var stale = CreateFolder("stale");
        var fixture = ClaudeFixture(live);
        fixture.ClaudeSessions = [new ClaudeCodeSession(20, "one", stale, null)];

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, live);
        Assert.Equal(0, fixture.ClaudeSessionReads);
    }

    [Fact]
    public void ClaudePointerCwdIsFallbackWhenProcessCwdCannotBeRead()
    {
        var folder = CreateFolder("pointer");
        var fixture = ClaudeFixture(folder);
        fixture.WorkingDirectories[20] = null;
        fixture.ClaudeSessions = [new ClaudeCodeSession(20, "one", folder, null)];

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, folder);
        Assert.Equal(1, fixture.ClaudeSessionReads);
    }

    [Fact]
    public void MultipleClaudePointerSessionsInSameDirectoryDeduplicate()
    {
        var folder = CreateFolder("pointer");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("Claude Code"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "claude.exe"),
                Process(30, 10, "claude.exe")),
            ClaudeSessions =
            [
                new ClaudeCodeSession(20, "one", folder, null),
                new ClaudeCodeSession(30, "two", folder, null),
            ],
            AccessibleText = _ => $"Claude Code\n{folder}",
        };
        fixture.WorkingDirectories[20] = null;
        fixture.WorkingDirectories[30] = null;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, folder);
    }

    [Fact]
    public void CodexSessionMetadataRemainsTitleMatchedFallback()
    {
        var folder = CreateFolder("project");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("status | project"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession("one", folder, DateTime.UtcNow)],
        };
        fixture.WorkingDirectories[20] = null;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
    }

    [Fact]
    public void SameBasenameCodexPathsRemainAmbiguous()
    {
        var first = CreateFolder(Path.Combine("one", "repo"));
        var second = CreateFolder(Path.Combine("two", "repo"));
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("status | repo"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe"),
                Process(30, 10, "codex.exe")),
        };
        fixture.WorkingDirectories[20] = first;
        fixture.WorkingDirectories[30] = second;

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public void WindowsTerminalFooterResolvesCodexWithoutVisibleWelcomeMarker()
    {
        var folder = CreateFolder("vibeclown");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("vibeclown"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            AccessibleText = _ => $"long conversation\ngpt-5.6-sol high · {folder}",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
        Assert.Equal(1, fixture.CodexSessionReads);
    }

    [Fact]
    public async Task ResumeUsesSessionWorkingDirectoryInsteadOfStaleProcessCwd()
    {
        var launch = CreateFolder("launch");
        var resumed = CreateFolder("resumed");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("resumed"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession("resumed-session", resumed, DateTime.UtcNow)],
            AccessibleText = _ => $"ordinary conversation\nmodel high · {resumed}",
        };
        fixture.WorkingDirectories[20] = launch;
        var source = new TerminalAgentWorkspaceSource(fixture.Resolver);
        var window = new IntPtr(1);

        Assert.True(source.MatchesForeground(window));
        Assert.Equal(
            resumed,
            (await source.GetActiveFolderAsync(CancellationToken.None))?.Path,
            ignoreCase: true);
        Assert.Equal(2, fixture.CodexSessionReads);
    }

    [Fact]
    public async Task WindowsTerminalNestedGitResumeCompletesSourceLifecycle()
    {
        var root = CreateFolder("windows-terminal-repo");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var launch = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("windows-terminal-repo"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), resumed, DateTime.UtcNow, launch),
            ],
            AccessibleText = _ => $"ordinary conversation\nmodel high · {resumed}",
        };
        fixture.WorkingDirectories[20] = launch;
        var source = new TerminalAgentWorkspaceSource(fixture.Resolver);

        Assert.True(source.MatchesForeground(new IntPtr(1)));
        Assert.Equal(
            resumed,
            (await source.GetActiveFolderAsync(CancellationToken.None))?.Path,
            ignoreCase: true);
        Assert.Equal(2, fixture.CodexSessionReads);
    }

    [Fact]
    public void ActiveThreadIdResolvesWithCustomizedStatusLine()
    {
        var folder = CreateFolder("thread-project");
        var sessionId = Guid.NewGuid().ToString();
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal($"custom | {sessionId} | title"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession(sessionId, folder, DateTime.UtcNow)],
            AccessibleText = _ => "conversation only",
        };
        fixture.WorkingDirectories[20] = CreateFolder("stale-launch");

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
        Assert.Equal(2, fixture.CodexSessionReads);
    }

    [Fact]
    public void CodexTruncatedThreadIdResolvesWithActualTitleFormat()
    {
        var folder = CreateFolder("truncated-thread-project");
        var sessionId = "12345678-1234-1234-1234-123456789abc";
        var titleId = CodexCodeWindowMatcher.ThreadTitleValue(sessionId);
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal($"codex | truncated-thread-project | {titleId}"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession(sessionId, folder, DateTime.UtcNow)],
            AccessibleText = _ => "conversation only",
        };
        fixture.WorkingDirectories[20] = CreateFolder("stale-truncated-launch");

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
    }

    [Fact]
    public void ThreadIdDisambiguatesSessionsWithSameWorkingDirectory()
    {
        var folder = CreateFolder("shared");
        var moved = CreateFolder("moved");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var title = $"shared | {second}";
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal(title),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe"),
                Process(30, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(first, folder, DateTime.UtcNow),
                new CodexCliSession(second, folder, DateTime.UtcNow),
            ],
            AccessibleText = _ => "conversation",
        };
        fixture.WorkingDirectories[20] = folder;
        fixture.WorkingDirectories[30] = folder;

        var resolver = fixture.Resolver;
        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);

        title = "moved";
        fixture.CodexSessions =
        [
            new CodexCliSession(first, folder, DateTime.UtcNow),
            new CodexCliSession(second, moved, DateTime.UtcNow),
        ];

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, moved);
        Assert.Equal(2, fixture.CodexSessionReads);
    }

    [Fact]
    public void ActivityTitleAndExactFooterResolveSharedSessionWorkspace()
    {
        var folder = CreateFolder("GrepFlow");
        var unrelated = CreateFolder("vibeclown");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("\u2839 GrepFlow"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
                new CodexCliSession(Guid.NewGuid().ToString(), unrelated, DateTime.UtcNow),
            ],
            AccessibleText = _ => $"conversation\ngpt-5.6-sol high \u00b7 {folder}",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
        Assert.Equal(1, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public async Task SharedSessionWorkspaceCompletesSourceLifecycle()
    {
        var folder = CreateFolder("GrepFlow-source");
        var unrelated = CreateFolder("unrelated-source");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("\u2839 GrepFlow-source"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
                new CodexCliSession(Guid.NewGuid().ToString(), unrelated, DateTime.UtcNow),
            ],
            AccessibleText = _ => $"conversation\ngpt-5.6-sol high \u00b7 {folder}",
        };
        fixture.WorkingDirectories[20] = folder;
        var source = new TerminalAgentWorkspaceSource(fixture.Resolver);
        var window = new IntPtr(1);

        Assert.True(source.MatchesForeground(window));
        Assert.Equal(
            folder,
            (await source.GetActiveFolderAsync(CancellationToken.None))?.Path,
            ignoreCase: true);
        Assert.Equal(1, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public void ProductMarkerAndUniqueTitlePathResolveSharedSessionWorkspace()
    {
        var folder = CreateFolder("shared-product");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("status | shared-product"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
                new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow),
            ],
            AccessibleText = _ => "OpenAI Codex\nconversation",
        };
        fixture.WorkingDirectories[20] = folder;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
        Assert.Equal(1, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public void PathOnlyResolutionDoesNotFollowEitherSessionAfterEvidenceDisappears()
    {
        var shared = CreateFolder("path-only-shared");
        var firstMoved = CreateFolder("path-only-first-moved");
        var secondMoved = CreateFolder("path-only-second-moved");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var showEvidence = true;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal(showEvidence ? "path-only-shared" : "conversation"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(first, shared, DateTime.UtcNow),
                new CodexCliSession(second, shared, DateTime.UtcNow),
            ],
            AccessibleText = _ => showEvidence
                ? $"conversation\ngpt-5.6-sol high \u00b7 {shared}"
                : "ordinary conversation",
        };
        fixture.WorkingDirectories[20] = shared;
        var resolver = fixture.Resolver;

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, shared);

        showEvidence = false;
        fixture.CodexSessions =
        [
            new CodexCliSession(first, firstMoved, DateTime.UtcNow),
            new CodexCliSession(second, secondMoved, DateTime.UtcNow),
        ];

        Assert.Null(resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(1, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public void PathOnlyResolutionClearsExistingConfirmedSession()
    {
        var shared = CreateFolder("cleared-shared");
        var moved = CreateFolder("cleared-moved");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var phase = 0;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal(phase switch
            {
                0 => first,
                1 => "cleared-shared",
                _ => "conversation",
            }),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession(first, shared, DateTime.UtcNow)],
            AccessibleText = _ => phase == 1
                ? $"conversation\ngpt-5.6-sol high \u00b7 {shared}"
                : "ordinary conversation",
        };
        fixture.WorkingDirectories[20] = shared;
        var resolver = fixture.Resolver;

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, shared);

        phase = 1;
        fixture.CodexSessions =
        [
            new CodexCliSession(first, shared, DateTime.UtcNow),
            new CodexCliSession(second, shared, DateTime.UtcNow),
        ];
        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, shared);

        phase = 2;
        fixture.CodexSessions =
        [
            new CodexCliSession(first, moved, DateTime.UtcNow),
            new CodexCliSession(second, shared, DateTime.UtcNow),
        ];

        Assert.Null(resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(2, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.CodexTargetedSessionReads);
    }

    [Fact]
    public void ConflictingThreadIdAndFooterPathFailClosed()
    {
        var firstFolder = CreateFolder("first-thread");
        var secondFolder = CreateFolder("second-thread");
        var logDirectory = CreateFolder("conflict-log");
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal($"{first} | second-thread"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions =
            [
                new CodexCliSession(first, firstFolder, DateTime.UtcNow),
                new CodexCliSession(second, secondFolder, DateTime.UtcNow),
            ],
            Log = new PluginLog(logDirectory),
            AccessibleText = _ => $"gpt-5.6-sol high · {secondFolder}",
        };
        fixture.WorkingDirectories[20] = firstFolder;

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Single(File.ReadLines(Path.Combine(logDirectory, "plugin.log")));
    }

    [Fact]
    public void ConfirmedWindowFollowsFreshSessionCwdAfterWelcomeScrollsAway()
    {
        var initial = CreateFolder("initial-thread");
        var resumed = CreateFolder("resumed-thread");
        var sessionId = Guid.NewGuid().ToString();
        var showWelcome = true;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal(showWelcome ? "initial-thread" : "resumed-thread"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession(sessionId, initial, DateTime.UtcNow)],
            AccessibleText = _ => showWelcome ? "OpenAI Codex" : "ordinary conversation",
        };
        fixture.WorkingDirectories[20] = initial;
        var resolver = fixture.Resolver;
        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, initial);

        showWelcome = false;
        fixture.CodexSessions = [new CodexCliSession(sessionId, resumed, DateTime.UtcNow)];

        AssertWorkspace(resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, resumed);
    }

    [Fact]
    public void InactiveConfirmedSessionIsNotReturned()
    {
        var folder = CreateFolder("confirmed");
        var sessionId = Guid.NewGuid().ToString();
        var showWelcome = true;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("confirmed"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession(sessionId, folder, DateTime.UtcNow)],
            AccessibleText = _ => showWelcome ? "OpenAI Codex" : "conversation",
        };
        fixture.WorkingDirectories[20] = folder;
        var resolver = fixture.Resolver;
        Assert.NotNull(resolver.TryResolve(new IntPtr(1)));

        showWelcome = false;
        fixture.CodexSessions = [];

        Assert.Null(resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public void CachedCodexWindowDoesNotClaimNormalShellTab()
    {
        var folder = CreateFolder("cached");
        var sessionId = Guid.NewGuid().ToString();
        var shell = false;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal(shell ? "Command Prompt" : "cached"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession(sessionId, folder, DateTime.UtcNow)],
            AccessibleText = _ => shell ? $"Command Prompt\n{folder}>" : "OpenAI Codex",
        };
        fixture.WorkingDirectories[20] = folder;
        var resolver = fixture.Resolver;
        Assert.NotNull(resolver.TryResolve(new IntPtr(1)));

        shell = true;

        Assert.False(resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Null(resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public void NormalCmdOutputAndPromptInCodexDirectoryRemainUnmatched()
    {
        var folder = CreateFolder("cmd-repo");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("cmd-repo"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            CodexSessions = [new CodexCliSession(Guid.NewGuid().ToString(), folder, DateTime.UtcNow)],
            AccessibleText = _ => $"{folder}\n{folder}>",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.False(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.ProjectAliasProbes);
    }

    [Fact]
    public void PromptLikeConversationBeforeFooterDoesNotRejectCodexWindow()
    {
        var folder = CreateFolder("prompt-conversation");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("prompt-conversation"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            AccessibleText = _ => $"assistant example\n{folder}>\nmodel high · {folder}",
        };
        fixture.WorkingDirectories[20] = folder;

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
    }

    [Fact]
    public void UnconfirmedNormalShellDoesNotProbeProjectParents()
    {
        var root = CreateFolder("unconfirmed-root");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            AccessibleText = _ => "Command Prompt\nC:\\>",
        };
        fixture.WorkingDirectories[20] = nested;

        Assert.False(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(0, fixture.ProjectAliasProbes);
        Assert.Equal(0, fixture.CodexSessionReads);
    }

    [Fact]
    public void NestedCodexCwdMatchesProjectRootTitle()
    {
        var root = CreateFolder("root-project");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("working | root-project"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
            AccessibleText = _ => $"conversation\ngpt-5.6-sol high · {nested}",
        };
        fixture.WorkingDirectories[20] = nested;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, nested);
    }

    [Fact]
    public void StaleTerminalProcessReturnsNull()
    {
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(99, "WindowsTerminal.exe", "project"),
            Snapshot = Snapshot(Process(20, 99, "codex.exe")),
        };

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public void DisappearedAgentProcessIsObservedOnNextResolution()
    {
        var folder = CreateFolder("project");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("project"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
        };
        fixture.WorkingDirectories[20] = folder;
        Assert.NotNull(fixture.Resolver.TryResolve(new IntPtr(1)));

        fixture.Snapshot = Snapshot(Process(10, 1, "WindowsTerminal.exe"));

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public void RecoverableCwdExceptionDoesNotPoisonLaterCall()
    {
        var folder = CreateFolder("project");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("project"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
        };
        fixture.WorkingDirectoryReader = _ => throw new InvalidOperationException("race");
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));

        fixture.WorkingDirectoryReader = _ => folder;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, folder);
    }

    [Fact]
    public void RecoverableAccessibleTextExceptionDoesNotPoisonLaterCall()
    {
        var folder = CreateFolder("project");
        var fixture = ClaudeFixture(folder);
        fixture.AccessibleText = _ => throw new InvalidOperationException("stale UIA");
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));

        fixture.AccessibleText = _ => $"Claude Code\n{folder}";

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, folder);
    }

    [Fact]
    public void RecoverableSessionExceptionDoesNotPoisonLaterCall()
    {
        var folder = CreateFolder("project");
        var fixture = ClaudeFixture(folder);
        fixture.WorkingDirectories[20] = null;
        fixture.ClaudeSessionReader = () => throw new IOException("lifecycle race");
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));

        fixture.ClaudeSessionReader = () => [new ClaudeCodeSession(20, "one", folder, null)];

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, folder);
    }

    [Fact]
    public void NonterminalWindowPerformsNoSnapshotOrReaderWork()
    {
        var fixture = new Fixture { Inspect = _ => null };

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
        Assert.Equal(0, fixture.SnapshotCaptures);
        Assert.Equal(0, fixture.CwdReads);
        Assert.Equal(0, fixture.CodexSessionReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);
        Assert.Equal(0, fixture.AccessibleTextReads);
    }

    [Fact]
    public void MultiplexedCodexRequiresTitleEvidenceEvenWithOneCandidate()
    {
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("Command Prompt"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe")),
        };
        fixture.WorkingDirectories[20] = CreateFolder("project");

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public void MixedDedicatedAgentsRequireAndCanUseAgentSpecificEvidence()
    {
        var codex = CreateFolder("codex-project");
        var claude = CreateFolder("claude-project");
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", "status | codex-project"),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe"),
                Process(30, 10, "claude.exe")),
            AccessibleText = _ => "OpenAI Codex",
        };
        fixture.WorkingDirectories[20] = codex;
        fixture.WorkingDirectories[30] = claude;

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.Codex, codex);
    }

    [Fact]
    public void ConflictingCodexAndClaudeEvidenceFailsClosed()
    {
        var codex = CreateFolder("codex-project");
        var claude = CreateFolder("claude-project");
        var fixture = SharedWindowsTerminalFixture(codex, claude);
        fixture.Inspect = _ => WindowsTerminal("status | codex-project");
        fixture.AccessibleText = _ => $"OpenAI Codex\nClaude Code\n{claude}";

        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
    }

    [Theory]
    [InlineData("Claude Code")]
    [InlineData("⠹ GrepFlow")]
    [InlineData("✳ Возможности плагина")]
    public void ClaudeResolvesFromHeaderEvidenceForInitialBusyAndIdleTitles(string title)
    {
        var folder = CreateFolder("dynamic-title-project");
        var fixture = ClaudeFixture(folder);
        fixture.Inspect = _ => WindowsTerminal(title);

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, folder);
    }

    [Fact]
    public void CheapDynamicClaudeMatchReadsOneUiaSnapshotAndNoSessions()
    {
        var folder = CreateFolder("dynamic-title-project");
        var fixture = ClaudeFixture(folder);
        fixture.Inspect = _ => WindowsTerminal("⠹ GrepFlow");

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(1, fixture.CwdReads);
        Assert.Equal(1, fixture.AccessibleTextReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);
        Assert.Equal(0, fixture.CodexSessionReads);
    }

    [Fact]
    public void UnreadableClaudeCwdCachesFromStrongHeaderThenResolvesPointer()
    {
        var folder = CreateFolder("pointer-project");
        var fixture = ClaudeFixture(folder);
        fixture.Inspect = _ => WindowsTerminal("✳ Generated session title");
        fixture.WorkingDirectories[20] = null;
        fixture.ClaudeSessions = [new ClaudeCodeSession(20, "one", folder, null)];

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(0, fixture.ClaudeSessionReads);

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, folder);
        Assert.Equal(1, fixture.ClaudeSessionReads);
    }

    [Fact]
    public void SelectedUnreadableClaudeCwdCachesWhenAnotherClaudeCwdIsReadable()
    {
        var projectA = CreateFolder("project-a");
        var projectB = CreateFolder("project-b");
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("✳ Generated session title"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "claude.exe"),
                Process(30, 10, "claude.exe")),
            ClaudeSessions = [new ClaudeCodeSession(30, "selected", projectB, null)],
            AccessibleText = _ => $"Claude Code\n{projectB}",
        };
        fixture.WorkingDirectories[20] = projectA;
        fixture.WorkingDirectories[30] = null;

        Assert.True(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(2, fixture.CwdReads);
        Assert.Equal(1, fixture.AccessibleTextReads);
        Assert.Equal(0, fixture.ClaudeSessionReads);

        AssertWorkspace(fixture.Resolver.TryResolve(new IntPtr(1)), TerminalAgentKind.ClaudeCode, projectB);
        Assert.Equal(1, fixture.ClaudeSessionReads);
    }

    [Fact]
    public void UnreadableClaudeCwdWithoutStrongHeaderDoesNotCache()
    {
        var fixture = ClaudeFixture(CreateFolder("project"));
        fixture.Inspect = _ => WindowsTerminal("Command Prompt");
        fixture.WorkingDirectories[20] = null;
        fixture.AccessibleText = _ => "Command Prompt\nC:\\>";

        Assert.False(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Equal(0, fixture.ClaudeSessionReads);
    }

    [Fact]
    public void NormalCmdTabRemainsUnmatchedWhileDynamicClaudeRunsElsewhere()
    {
        var folder = CreateFolder("claude-project");
        var fixture = ClaudeFixture(folder);
        fixture.Inspect = _ => WindowsTerminal("Command Prompt claude-project");
        fixture.AccessibleText = _ => $"Command Prompt\n{folder}>";

        Assert.False(fixture.Resolver.TryMatchForeground(new IntPtr(1)));
        Assert.Null(fixture.Resolver.TryResolve(new IntPtr(1)));
    }

    [Fact]
    public async Task StoredHwndFreshlyFollowsNormalAndDynamicClaudeTabs()
    {
        var folder = CreateFolder("claude-project");
        var fixture = ClaudeFixture(folder);
        var dynamicClaudeSelected = true;
        fixture.Inspect = _ => WindowsTerminal(dynamicClaudeSelected ? "⠹ GrepFlow" : "Command Prompt");
        fixture.AccessibleText = _ => dynamicClaudeSelected
            ? $"Claude Code\n{folder}"
            : $"Command Prompt\n{folder}>";
        var source = new TerminalAgentWorkspaceSource(fixture.Resolver);
        var window = new IntPtr(1);
        Assert.True(source.MatchesForeground(window));
        Assert.Equal(folder, (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        dynamicClaudeSelected = false;
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));

        dynamicClaudeSelected = true;
        Assert.Equal(folder, (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private Fixture DedicatedCodexFixture(string title, string first, string second)
    {
        var fixture = new Fixture
        {
            Inspect = _ => new TerminalWindow(10, "cmd.exe", title),
            Snapshot = Snapshot(
                Process(10, 1, "cmd.exe"),
                Process(20, 10, "codex.exe"),
                Process(30, 10, "codex.exe")),
        };
        fixture.WorkingDirectories[20] = first;
        fixture.WorkingDirectories[30] = second;
        return fixture;
    }

    private Fixture SharedWindowsTerminalFixture(string codex, string claude)
    {
        var fixture = new Fixture
        {
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "codex.exe"),
                Process(30, 10, "claude.exe")),
        };
        fixture.WorkingDirectories[20] = codex;
        fixture.WorkingDirectories[30] = claude;
        return fixture;
    }

    private Fixture ClaudeFixture(string folder)
    {
        var fixture = new Fixture
        {
            Inspect = _ => WindowsTerminal("Claude Code"),
            Snapshot = Snapshot(
                Process(10, 1, "WindowsTerminal.exe"),
                Process(20, 10, "claude.exe")),
            AccessibleText = _ => $"Claude Code\n{folder}",
        };
        fixture.WorkingDirectories[20] = folder;
        return fixture;
    }

    private string CreateFolder(string name)
        => Directory.CreateDirectory(Path.Combine(_temporaryDirectory, name)).FullName;

    private static TerminalWindow WindowsTerminal(string title)
        => new(10, "WindowsTerminal.exe", title);

    private static WindowsProcessSnapshot Snapshot(params WindowsProcessInfo[] processes)
    {
        var result = processes.ToList();
        var nextProcessId = result.Count == 0 ? 1u : result.Max(process => process.ProcessId) + 1;
        foreach (var terminal in result
                     .Where(process => string.Equals(
                         process.ImageFileName,
                         "WindowsTerminal.exe",
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var directAgentIndexes = result
                .Select((process, index) => (process, index))
                .Where(item =>
                    item.process.ParentProcessId == terminal.ProcessId &&
                    (string.Equals(item.process.ImageFileName, "codex.exe", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.process.ImageFileName, "claude.exe", StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.index)
                .ToArray();
            if (directAgentIndexes.Length == 0) continue;

            var commandShellId = nextProcessId++;
            result.Add(Process(commandShellId, terminal.ProcessId, "cmd.exe"));
            foreach (var index in directAgentIndexes)
                result[index] = result[index] with { ParentProcessId = commandShellId };
        }

        return new WindowsProcessSnapshot(result);
    }

    private static WindowsProcessInfo Process(uint id, uint parent, string image)
        => new(id, parent, image);

    private static void AssertWorkspace(
        TerminalAgentWorkspace? actual,
        TerminalAgentKind kind,
        string path)
    {
        Assert.NotNull(actual);
        Assert.Equal(kind, actual.Kind);
        Assert.Equal(path, actual.WorkingDirectory, ignoreCase: true);
    }

    private sealed class Fixture
    {
        private readonly TerminalAgentProcessLocator _locator = new();
        private readonly CodexCodeWindowMatcher _codexMatcher = new();
        private readonly ClaudeCodeWindowMatcher _matcher = new();
        private TerminalAgentForegroundResolver? _resolver;

        public Func<IntPtr, TerminalWindow?> Inspect { get; set; } = _ => null;
        public WindowsProcessSnapshot Snapshot { get; set; } = new([]);
        public Dictionary<uint, string?> WorkingDirectories { get; } = [];
        public Func<uint, string?>? WorkingDirectoryReader { get; set; }
        public IReadOnlyList<CodexCliSession> CodexSessions { get; set; } = [];
        public IReadOnlyList<ClaudeCodeSession> ClaudeSessions { get; set; } = [];
        public Func<IReadOnlyList<ClaudeCodeSession>>? ClaudeSessionReader { get; set; }
        public Func<IntPtr, string?> AccessibleText { get; set; } = _ => "OpenAI Codex";
        public PluginLog? Log { get; set; }
        public int SnapshotCaptures { get; private set; }
        public int CwdReads { get; private set; }
        public int CodexSessionReads { get; private set; }
        public int CodexTargetedSessionReads { get; private set; }
        public int ClaudeSessionReads { get; private set; }
        public int AccessibleTextReads { get; private set; }
        public int ProjectAliasProbes => _codexMatcher.ProjectAliasProbeCount;

        public TerminalAgentForegroundResolver Resolver => _resolver ??= new(
            window => Inspect(window),
            () =>
            {
                SnapshotCaptures++;
                return Snapshot;
            },
            _locator.Locate,
            pid =>
            {
                CwdReads++;
                return WorkingDirectoryReader is null
                    ? WorkingDirectories.GetValueOrDefault(pid)
                    : WorkingDirectoryReader(pid);
            },
            () =>
            {
                CodexSessionReads++;
                return CodexSessions;
            },
            sessionId =>
            {
                CodexTargetedSessionReads++;
                return CodexSessions.SingleOrDefault(session => string.Equals(
                    session.SessionId,
                    sessionId,
                    StringComparison.OrdinalIgnoreCase));
            },
            () =>
            {
                ClaudeSessionReads++;
                return ClaudeSessionReader is null ? ClaudeSessions : ClaudeSessionReader();
            },
            window =>
            {
                AccessibleTextReads++;
                return AccessibleText(window);
            },
            _codexMatcher,
            _matcher.HasHeaderEvidence,
            _matcher.Match,
            Log);
    }
}
