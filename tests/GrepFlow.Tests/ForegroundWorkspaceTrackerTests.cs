using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class ForegroundWorkspaceTrackerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-Tracker-{Guid.NewGuid():N}");

    public ForegroundWorkspaceTrackerTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public async Task InitialNormalTabBecomingAgentInSameHwndIsDetectedWhenFlowGainsFocus()
    {
        var terminalWindow = new IntPtr(101);
        var flowWindow = new IntPtr(202);
        var agentVisible = false;
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "project")).FullName;
        var source = new TerminalAgentWorkspaceSource(
            window => window == terminalWindow && agentVisible,
            window => window == terminalWindow
                ? new TerminalAgentWorkspace(TerminalAgentKind.ClaudeCode, folder)
                : null);
        using var dispatcher = new StaDispatcher();
        using var tracker = new ForegroundWorkspaceTracker(
            dispatcher,
            [source],
            new ExplorerHwndCache());

        tracker.CaptureForeground(terminalWindow);
        Assert.Null(tracker.LastSourceId);

        agentVisible = true;
        tracker.CaptureForeground(flowWindow);

        Assert.Equal(TerminalAgentWorkspaceSource.SourceId, tracker.LastSourceId);
        var active = await source.GetActiveFolderAsync(CancellationToken.None);
        Assert.Equal(folder, active?.Path);
        Assert.Equal("Claude Code", active?.SourceName);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
