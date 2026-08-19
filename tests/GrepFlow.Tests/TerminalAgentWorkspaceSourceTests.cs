using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class TerminalAgentWorkspaceSourceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-AgentSource-{Guid.NewGuid():N}");

    public TerminalAgentWorkspaceSourceTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task GetBeforeForegroundMatchDoesNotCallResolver()
    {
        var calls = 0;
        var source = new TerminalAgentWorkspaceSource(_ => true, _ =>
        {
            calls++;
            return Workspace(TerminalAgentKind.Codex, CreateFolder("project"));
        });

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SuccessfulMatchStoresOnlyHwndAndFreshlyResolvesPath()
    {
        var first = CreateFolder("first");
        var second = CreateFolder("second");
        var current = first;
        var matchedWindows = new List<IntPtr>();
        var resolvedWindows = new List<IntPtr>();
        var source = new TerminalAgentWorkspaceSource(
            window =>
            {
                matchedWindows.Add(window);
                return true;
            },
            window =>
            {
                resolvedWindows.Add(window);
                return Workspace(TerminalAgentKind.Codex, current);
            });
        var window = new IntPtr(42);
        Assert.True(source.MatchesForeground(window));
        Assert.Empty(resolvedWindows);

        current = second;
        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal([window], matchedWindows);
        Assert.Equal([window], resolvedWindows);
        Assert.Equal(second, active?.Path);
    }

    [Fact]
    public async Task CachedHwndReturnsUpdatedCodexSessionPathAfterResume()
    {
        var launch = CreateFolder("launch");
        var resumed = CreateFolder("resumed");
        var currentSessionPath = launch;
        var source = new TerminalAgentWorkspaceSource(
            _ => true,
            _ => Workspace(TerminalAgentKind.Codex, currentSessionPath));
        var window = new IntPtr(42);
        Assert.True(source.MatchesForeground(window));
        Assert.Equal(launch, (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        currentSessionPath = resumed;

        Assert.Equal(resumed, (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Theory]
    [InlineData(TerminalAgentKind.Codex, "Codex CLI")]
    [InlineData(TerminalAgentKind.ClaudeCode, "Claude Code")]
    public async Task FreshResolutionUsesAgentSpecificSourceName(
        TerminalAgentKind kind,
        string expectedName)
    {
        var folder = CreateFolder(kind.ToString());
        var source = new TerminalAgentWorkspaceSource(_ => true, _ => Workspace(kind, folder));
        Assert.True(source.MatchesForeground(new IntPtr(42)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(expectedName, active?.SourceName);
        Assert.Equal("Terminal agent", source.DisplayName);
        Assert.Equal("terminal-agent", source.Id);
    }

    [Fact]
    public async Task MostRecentlyMatchedWindowWins()
    {
        var first = CreateFolder("first");
        var second = CreateFolder("second");
        var source = new TerminalAgentWorkspaceSource(
            _ => true,
            window => window == new IntPtr(1)
            ? Workspace(TerminalAgentKind.Codex, first)
            : Workspace(TerminalAgentKind.ClaudeCode, second));

        Assert.True(source.MatchesForeground(new IntPtr(1)));
        Assert.True(source.MatchesForeground(new IntPtr(2)));

        Assert.Equal(second, (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task SameHwndObservesAgentAndPathChanges()
    {
        var codex = CreateFolder("codex");
        var claude = CreateFolder("claude");
        var current = Workspace(TerminalAgentKind.Codex, codex);
        var source = new TerminalAgentWorkspaceSource(_ => true, _ => current);
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        Assert.Equal("Codex CLI", (await source.GetActiveFolderAsync(CancellationToken.None))?.SourceName);

        current = Workspace(TerminalAgentKind.ClaudeCode, claude);
        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(claude, active?.Path);
        Assert.Equal("Claude Code", active?.SourceName);
    }

    [Fact]
    public async Task FreshFailureDoesNotReturnPriorPath()
    {
        TerminalAgentWorkspace? current = Workspace(
            TerminalAgentKind.Codex,
            CreateFolder("project"));
        var source = new TerminalAgentWorkspaceSource(_ => true, _ => current);
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        Assert.NotNull(await source.GetActiveFolderAsync(CancellationToken.None));

        current = null;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedForegroundDoesNotReplaceLastSuccessfulHwnd()
    {
        var folder = CreateFolder("project");
        var source = new TerminalAgentWorkspaceSource(
            window => window == new IntPtr(1),
            _ => Workspace(TerminalAgentKind.Codex, folder));
        Assert.True(source.MatchesForeground(new IntPtr(1)));

        Assert.False(source.MatchesForeground(new IntPtr(2)));

        Assert.Equal(folder, (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task DeletedResolvedFolderIsNotReturned()
    {
        var folder = CreateFolder("project");
        var source = new TerminalAgentWorkspaceSource(
            _ => true,
            _ => Workspace(TerminalAgentKind.Codex, folder));
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        Directory.Delete(folder);

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsHonoredBeforeResolution()
    {
        var calls = 0;
        var folder = CreateFolder("project");
        var source = new TerminalAgentWorkspaceSource(_ => true, _ =>
        {
            calls++;
            return Workspace(TerminalAgentKind.Codex, folder);
        });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetActiveFolderAsync(cancellation.Token).AsTask());
        Assert.Equal(0, calls);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private string CreateFolder(string name)
        => Directory.CreateDirectory(Path.Combine(_temporaryDirectory, name)).FullName;

    private static TerminalAgentWorkspace Workspace(TerminalAgentKind kind, string path)
        => new(kind, path);
}
