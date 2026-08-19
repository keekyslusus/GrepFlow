using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class FilesWorkspaceSourceTests
{
    [Fact]
    public async Task NeverActivatedSourceDoesNotReadCurrentPath()
    {
        var readCalls = 0;
        var source = new FilesWorkspaceSource(
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
    public async Task ActivatedSourceReadsPathFromMatchedWindow()
    {
        var filesWindow = new IntPtr(42);
        var readWindow = IntPtr.Zero;
        var source = new FilesWorkspaceSource(
            window => window == filesWindow,
            (window, _) =>
            {
                readWindow = window;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });

        Assert.True(source.MatchesForeground(filesWindow));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(filesWindow, readWindow);
        Assert.Equal(@"C:\workspace", active?.Path);
        Assert.Equal("Files", active?.SourceName);
    }

    [Fact]
    public async Task ClosedFilesWindowDoesNotReadCurrentPath()
    {
        var windowExists = true;
        var readCalls = 0;
        var source = new FilesWorkspaceSource(
            _ => windowExists,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(@"C:\workspace");
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        windowExists = false;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task OtherForegroundWindowDoesNotDiscardLastFilesWindow()
    {
        var filesWindow = new IntPtr(42);
        var source = new FilesWorkspaceSource(
            window => window == filesWindow,
            (_, _) => ValueTask.FromResult<string?>(@"C:\workspace"));
        Assert.True(source.MatchesForeground(filesWindow));

        Assert.False(source.MatchesForeground(new IntPtr(84)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);
        Assert.Equal(@"C:\workspace", active?.Path);
    }

    [Fact]
    public async Task MostRecentlyFocusedFilesWindowProvidesItsOwnPath()
    {
        var first = new IntPtr(42);
        var second = new IntPtr(84);
        var paths = new Dictionary<IntPtr, string>
        {
            [first] = @"C:\first",
            [second] = @"C:\second",
        };
        var source = new FilesWorkspaceSource(
            paths.ContainsKey,
            (window, _) => ValueTask.FromResult<string?>(paths[window]));

        Assert.True(source.MatchesForeground(first));
        Assert.Equal(@"C:\first", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        Assert.True(source.MatchesForeground(second));
        Assert.Equal(@"C:\second", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Theory]
    [InlineData("Files.exe", "WinUIDesktopWin32WindowClass", true)]
    [InlineData("files.EXE", "WinUIDesktopWin32WindowClass", true)]
    [InlineData("FilePilot.exe", "WinUIDesktopWin32WindowClass", false)]
    [InlineData("Files.exe", "ApplicationFrameWindow", false)]
    public void WindowIdentityRequiresFilesProcessAndWinUiClass(
        string processImageName,
        string windowClassName,
        bool expected)
    {
        Assert.Equal(
            expected,
            FilesWorkspaceSource.MatchesWindowIdentity(processImageName, windowClassName));
    }
}
