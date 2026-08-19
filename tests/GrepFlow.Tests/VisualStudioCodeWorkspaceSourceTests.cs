using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class VisualStudioCodeWorkspaceSourceTests
{
    [Fact]
    public async Task NeverActivatedSourceDoesNotReadSession()
    {
        var readCalls = 0;
        var source = new VisualStudioCodeWorkspaceSource(
            _ => true,
            () =>
            {
                readCalls++;
                return @"C:\workspace";
            });

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Null(active);
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task ActivatedSourceReadsLastActiveFolder()
    {
        var visualStudioCodeWindow = new IntPtr(42);
        var source = new VisualStudioCodeWorkspaceSource(
            window => window == visualStudioCodeWindow,
            () => @"C:\workspace");

        Assert.True(source.MatchesForeground(visualStudioCodeWindow));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(@"C:\workspace", active?.Path);
        Assert.Equal("Visual Studio Code", active?.SourceName);
    }

    [Fact]
    public async Task ClosedVisualStudioCodeWindowDoesNotReadSession()
    {
        var windowExists = true;
        var readCalls = 0;
        var source = new VisualStudioCodeWorkspaceSource(
            _ => windowExists,
            () =>
            {
                readCalls++;
                return @"C:\workspace";
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        windowExists = false;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task OtherForegroundWindowDoesNotDiscardLastVisualStudioCodeWindow()
    {
        var visualStudioCodeWindow = new IntPtr(42);
        var source = new VisualStudioCodeWorkspaceSource(
            window => window == visualStudioCodeWindow,
            () => @"C:\workspace");
        Assert.True(source.MatchesForeground(visualStudioCodeWindow));

        Assert.False(source.MatchesForeground(new IntPtr(84)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);
        Assert.Equal(@"C:\workspace", active?.Path);
    }
}
