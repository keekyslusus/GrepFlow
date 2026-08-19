using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class VisualStudioWorkspaceSourceTests
{
    [Fact]
    public async Task NeverActivatedSourceDoesNotInvokeReader()
    {
        var readCalls = 0;
        var source = new VisualStudioWorkspaceSource(
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
    public async Task ActivatedSourceReturnsReaderWorkspace()
    {
        var visualStudioWindow = new IntPtr(42);
        var readWindow = IntPtr.Zero;
        var source = new VisualStudioWorkspaceSource(
            window => window == visualStudioWindow,
            (window, _) =>
            {
                readWindow = window;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });

        Assert.True(source.MatchesForeground(visualStudioWindow));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(@"C:\workspace", active?.Path);
        Assert.Equal("Visual Studio", active?.SourceName);
        Assert.Equal(visualStudioWindow, readWindow);
    }

    [Fact]
    public async Task OtherForegroundWindowDoesNotDiscardLastVisualStudioWindow()
    {
        var visualStudioWindow = new IntPtr(42);
        var source = new VisualStudioWorkspaceSource(
            window => window == visualStudioWindow,
            (_, _) => ValueTask.FromResult<string?>(@"C:\workspace"));
        Assert.True(source.MatchesForeground(visualStudioWindow));

        Assert.False(source.MatchesForeground(new IntPtr(84)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);
        Assert.Equal(@"C:\workspace", active?.Path);
    }

    [Fact]
    public async Task ClosedOrReusedWindowClearsCacheWithoutInvokingReader()
    {
        var windowIsVisualStudio = true;
        var readCalls = 0;
        var source = new VisualStudioWorkspaceSource(
            _ => windowIsVisualStudio,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        windowIsVisualStudio = false;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task AlreadyCancelledLookupDoesNotInvokeReader()
    {
        var readCalls = 0;
        var source = new VisualStudioWorkspaceSource(
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
            async () => await source.GetActiveFolderAsync(cancellation.Token));
        Assert.Equal(0, readCalls);
    }
}
