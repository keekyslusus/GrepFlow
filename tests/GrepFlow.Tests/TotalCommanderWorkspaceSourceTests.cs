using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class TotalCommanderWorkspaceSourceTests
{
    [Fact]
    public async Task NeverActivatedSourceDoesNotReadActivePanel()
    {
        var readCalls = 0;
        var source = new TotalCommanderWorkspaceSource(
            _ => true,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Null(active);
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task MatchedWindowSuppliesItsActivePanel()
    {
        var totalCommanderWindow = new IntPtr(42);
        var readWindow = IntPtr.Zero;
        var source = new TotalCommanderWorkspaceSource(
            window => window == totalCommanderWindow,
            (window, _) =>
            {
                readWindow = window;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });

        Assert.True(source.MatchesForeground(totalCommanderWindow));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(totalCommanderWindow, readWindow);
        Assert.Equal(@"C:\workspace", active?.Path);
        Assert.Equal("Total Commander", active?.SourceName);
        Assert.False(active?.FromNearestWindow);
    }

    [Fact]
    public async Task RepeatedReadsQueryTheCurrentPanelInsteadOfCachingAPath()
    {
        var paths = new Queue<string>([@"C:\left", @"C:\right"]);
        var source = new TotalCommanderWorkspaceSource(
            _ => true,
            (_, _) => ValueTask.FromResult<string?>(paths.Dequeue()));
        Assert.True(source.MatchesForeground(new IntPtr(42)));

        Assert.Equal(@"C:\left", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
        Assert.Equal(@"C:\right", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
        Assert.Empty(paths);
    }

    [Fact]
    public async Task ClosedOrIdentityChangedWindowIsClearedWithoutReading()
    {
        var windowIsTotalCommander = true;
        var readCalls = 0;
        var source = new TotalCommanderWorkspaceSource(
            _ => windowIsTotalCommander,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        windowIsTotalCommander = false;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task UnrelatedForegroundWindowDoesNotDiscardLastTotalCommanderWindow()
    {
        var totalCommanderWindow = new IntPtr(42);
        var source = new TotalCommanderWorkspaceSource(
            window => window == totalCommanderWindow,
            (_, _) => ValueTask.FromResult<string?>(@"C:\workspace"));
        Assert.True(source.MatchesForeground(totalCommanderWindow));

        Assert.False(source.MatchesForeground(new IntPtr(84)));

        Assert.Equal(
            @"C:\workspace",
            (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task MostRecentlyMatchedInstanceSuppliesItsOwnActivePanel()
    {
        var first = new IntPtr(42);
        var second = new IntPtr(84);
        var paths = new Dictionary<IntPtr, string>
        {
            [first] = @"C:\first",
            [second] = @"C:\second",
        };
        var source = new TotalCommanderWorkspaceSource(
            paths.ContainsKey,
            (window, _) => ValueTask.FromResult<string?>(paths[window]));

        Assert.True(source.MatchesForeground(first));
        Assert.Equal(@"C:\first", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        Assert.True(source.MatchesForeground(second));
        Assert.Equal(@"C:\second", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task CancellationBeforeReadDoesNotInvokeReader()
    {
        var readCalls = 0;
        var source = new TotalCommanderWorkspaceSource(
            _ => true,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetActiveFolderAsync(cancellation.Token).AsTask());
        Assert.Equal(0, readCalls);
    }

    [Theory]
    [InlineData("TOTALCMD.EXE", "TTOTAL_CMD", true)]
    [InlineData("totalcmd.exe", "TTOTAL_CMD", true)]
    [InlineData("TOTALCMD64.EXE", "TTOTAL_CMD", true)]
    [InlineData("totalcmd64.exe", "TTOTAL_CMD", true)]
    [InlineData("explorer.exe", "TTOTAL_CMD", false)]
    [InlineData("TOTALCMD64.EXE", "TTotal_Cmd", false)]
    [InlineData("TOTALCMD64.EXE", "TMessageForm", false)]
    [InlineData("TOTALCMD64.EXE", "TButton", false)]
    [InlineData(null, "TTOTAL_CMD", false)]
    [InlineData("TOTALCMD64.EXE", null, false)]
    public void WindowIdentityRequiresTotalCommanderMainWindow(
        string? processImageName,
        string? windowClassName,
        bool expected)
    {
        Assert.Equal(
            expected,
            TotalCommanderWorkspaceSource.MatchesWindowIdentity(processImageName, windowClassName));
    }
}
