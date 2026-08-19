using System.IO;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class CursorWorkspaceSourceTests : IDisposable
{
    private readonly string _folder;

    public CursorWorkspaceSourceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public async Task NeverActivatedSourceDoesNotReadWorkspace()
    {
        var readCalls = 0;
        var source = new CursorWorkspaceSource(
            _ => true,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(_folder);
            });

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task NonCursorForegroundDoesNotActivateSource()
    {
        var readCalls = 0;
        var source = new CursorWorkspaceSource(
            _ => false,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(_folder);
            });

        Assert.False(source.MatchesForeground(new IntPtr(42)));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task MatchedWindowIsReadAndReturnedAsCursorFolder()
    {
        var expectedWindow = new IntPtr(42);
        var observedWindow = IntPtr.Zero;
        var source = new CursorWorkspaceSource(
            window => window == expectedWindow,
            (window, _) =>
            {
                observedWindow = window;
                return ValueTask.FromResult<string?>(_folder);
            });

        Assert.True(source.MatchesForeground(expectedWindow));
        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(expectedWindow, observedWindow);
        Assert.Equal(_folder, active?.Path);
        Assert.Equal("Cursor", active?.SourceName);
        Assert.False(active?.FromNearestWindow);
    }

    [Fact]
    public async Task ClosedOrReusedWindowIsInvalidatedWithoutWorkspaceRead()
    {
        var cursorExists = true;
        var readCalls = 0;
        var source = new CursorWorkspaceSource(
            _ => cursorExists,
            (_, _) =>
            {
                readCalls++;
                return ValueTask.FromResult<string?>(_folder);
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        cursorExists = false;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task OtherForegroundDoesNotDiscardLastCursorWindow()
    {
        var cursorWindow = new IntPtr(42);
        var source = new CursorWorkspaceSource(
            window => window == cursorWindow,
            (_, _) => ValueTask.FromResult<string?>(_folder));
        Assert.True(source.MatchesForeground(cursorWindow));

        Assert.False(source.MatchesForeground(new IntPtr(84)));

        Assert.Equal(_folder, (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task LaterCursorWindowReplacesEarlierWindow()
    {
        var first = new IntPtr(42);
        var second = new IntPtr(84);
        var observed = IntPtr.Zero;
        var source = new CursorWorkspaceSource(
            window => window == first || window == second,
            (window, _) =>
            {
                observed = window;
                return ValueTask.FromResult<string?>(_folder);
            });

        Assert.True(source.MatchesForeground(first));
        Assert.True(source.MatchesForeground(second));
        _ = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(second, observed);
    }

    [Fact]
    public async Task MissingDirectoryIsRejected()
    {
        var source = new CursorWorkspaceSource(
            _ => true,
            (_, _) => ValueTask.FromResult<string?>(Path.Combine(_folder, "missing")));
        Assert.True(source.MatchesForeground(new IntPtr(42)));

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsCheckedBeforeAndAfterRead()
    {
        var source = new CursorWorkspaceSource(
            _ => true,
            (_, token) =>
            {
                if (!token.IsCancellationRequested) throw new InvalidOperationException("expected cancellation");
                return ValueTask.FromResult<string?>(_folder);
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetActiveFolderAsync(before.Token).AsTask());

        using var after = new CancellationTokenSource();
        var afterSource = new CursorWorkspaceSource(
            _ => true,
            (_, _) =>
            {
                after.Cancel();
                return ValueTask.FromResult<string?>(_folder);
            });
        Assert.True(afterSource.MatchesForeground(new IntPtr(42)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => afterSource.GetActiveFolderAsync(after.Token).AsTask());
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
