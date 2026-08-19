using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class ZedWorkspaceSourceTests
{
    private static readonly IntPtr FirstHwnd = new(42);
    private static readonly IntPtr SecondHwnd = new(84);

    [Fact]
    public async Task NeverActivatedSourcePerformsNoInspectionOrStateRead()
    {
        var inspectCalls = 0;
        var stateCalls = 0;
        var source = Source(
            window => { inspectCalls++; return Snapshot(window, 1, "repo"); },
            _ => { stateCalls++; return @"C:\repo"; });

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, inspectCalls);
        Assert.Equal(0, stateCalls);
    }

    [Fact]
    public async Task MatchedWindowReturnsResolvedZedFolder()
    {
        var source = Source(
            window => Snapshot(window, 1, "repo"),
            title => title == "repo" ? @"C:\repo" : null);

        Assert.True(source.MatchesForeground(FirstHwnd));
        var folder = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(@"C:\repo", folder?.Path);
        Assert.Equal("Zed", folder?.SourceName);
        Assert.False(folder?.FromNearestWindow);
    }

    [Fact]
    public async Task OtherForegroundApplicationDoesNotDiscardLastZedWindow()
    {
        var source = Source(
            window => window == FirstHwnd ? Snapshot(window, 1, "repo") : null,
            _ => @"C:\repo");
        Assert.True(source.MatchesForeground(FirstHwnd));

        Assert.False(source.MatchesForeground(SecondHwnd));

        Assert.Equal(@"C:\repo", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedOrReusedWindowClearsAssociationWithoutStateRead(bool reused)
    {
        var matched = false;
        var stateCalls = 0;
        var source = Source(
            window => !matched
                ? Snapshot(window, 1, "repo")
                : reused ? Snapshot(window, 2, "other") : null,
            _ => { stateCalls++; return @"C:\repo"; });
        Assert.True(source.MatchesForeground(FirstHwnd));
        matched = true;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, stateCalls);
    }

    [Fact]
    public async Task RefreshedTitleIsReadOnEveryQuery()
    {
        var title = "first";
        var seenTitles = new List<string>();
        var source = Source(
            window => Snapshot(window, 1, title),
            value =>
            {
                seenTitles.Add(value);
                return value == "first" ? @"C:\first" : @"C:\second";
            });
        source.MatchesForeground(FirstHwnd);

        Assert.Equal(@"C:\first", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
        title = "second";
        Assert.Equal(@"C:\second", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
        Assert.Equal(["first", "second"], seenTitles);
    }

    [Fact]
    public async Task StateReadFailureDoesNotReturnLastKnownFolder()
    {
        var readSucceeds = true;
        var source = Source(
            window => Snapshot(window, 1, "repo"),
            _ => readSucceeds ? @"C:\repo" : null);
        source.MatchesForeground(FirstHwnd);
        Assert.Equal(@"C:\repo", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        readSucceeds = false;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeResolutionAvoidsStateReader()
    {
        using var cancellation = new CancellationTokenSource();
        var inspectCalls = 0;
        var stateCalls = 0;
        var source = Source(
            window =>
            {
                inspectCalls++;
                if (inspectCalls == 2) cancellation.Cancel();
                return Snapshot(window, 1, "repo");
            },
            _ => { stateCalls++; return @"C:\repo"; });
        source.MatchesForeground(FirstHwnd);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.GetActiveFolderAsync(cancellation.Token));
        Assert.Equal(0, stateCalls);
    }

    [Fact]
    public async Task OlderInvalidQueryDoesNotEraseNewForegroundAssociation()
    {
        using var oldInspectionStarted = new ManualResetEventSlim();
        using var releaseOldInspection = new ManualResetEventSlim();
        var firstMatchComplete = false;
        var source = Source(
            window =>
            {
                if (window == FirstHwnd)
                {
                    if (!firstMatchComplete) return Snapshot(window, 1, "old");
                    oldInspectionStarted.Set();
                    releaseOldInspection.Wait(TimeSpan.FromSeconds(5));
                    return null;
                }

                return Snapshot(window, 2, "new");
            },
            title => title == "new" ? @"C:\new" : null);
        Assert.True(source.MatchesForeground(FirstHwnd));
        firstMatchComplete = true;

        var oldQuery = Task.Run(async () =>
            await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.True(oldInspectionStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(source.MatchesForeground(SecondHwnd));
        releaseOldInspection.Set();

        Assert.Null(await oldQuery);
        Assert.Equal(@"C:\new", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task MissingOrRemovedResolvedDirectoryReturnsNull()
    {
        var source = new ZedWorkspaceSource(
            window => Snapshot(window, 1, "repo"),
            _ => @"C:\missing",
            _ => false);
        source.MatchesForeground(FirstHwnd);

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
    }

    private static ZedWorkspaceSource Source(
        Func<IntPtr, ZedWindowSnapshot?> inspect,
        Func<string, string?> readState)
        => new(inspect, readState, _ => true);

    private static ZedWindowSnapshot Snapshot(IntPtr window, uint processId, string title)
        => new(window, processId, @"C:\Zed\Zed.exe", title, "Zed::Window");
}
