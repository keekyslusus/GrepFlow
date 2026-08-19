using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class FilePilotWorkspaceSourceTests
{
    [Fact]
    public async Task NeverActivatedSourceDoesNotReadSession()
    {
        var readCalls = 0;
        var source = new FilePilotWorkspaceSource(
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
    public async Task ActivatedSourceReadsCurrentSessionPath()
    {
        var source = new FilePilotWorkspaceSource(_ => true, () => @"C:\workspace");

        Assert.True(source.MatchesForeground(new IntPtr(42)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(@"C:\workspace", active?.Path);
        Assert.Equal("FilePilot", active?.SourceName);
    }

    [Fact]
    public async Task ClosedFilePilotWindowDoesNotReadSession()
    {
        var windowExists = true;
        var readCalls = 0;
        var source = new FilePilotWorkspaceSource(
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
    public async Task OtherForegroundWindowDoesNotDiscardLastFilePilotWindow()
    {
        var filePilotWindow = new IntPtr(42);
        var source = new FilePilotWorkspaceSource(
            window => window == filePilotWindow,
            () => @"C:\workspace");
        Assert.True(source.MatchesForeground(filePilotWindow));

        Assert.False(source.MatchesForeground(new IntPtr(84)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);
        Assert.Equal(@"C:\workspace", active?.Path);
    }
}
