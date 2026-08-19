using System.IO;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class CursorWorkspaceReaderTests : IDisposable
{
    private readonly string _folder;

    public CursorWorkspaceReaderTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public async Task ReclassifiesGlassAndIdeOnEveryRead()
    {
        var mode = CursorWindowMode.Glass;
        var reader = new CursorWorkspaceReader(
            _ => new CursorWindowSnapshot(mode, mode.ToString()),
            snapshot => snapshot.Mode == mode ? _folder : null,
            TimeSpan.FromSeconds(1));

        Assert.Equal(_folder, await reader.TryReadActiveFolderAsync(new IntPtr(42), CancellationToken.None));
        mode = CursorWindowMode.Ide;
        Assert.Equal(_folder, await reader.TryReadActiveFolderAsync(new IntPtr(42), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsWaitingForBackgroundRead()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new CursorWorkspaceReader(
            _ =>
            {
                started.Set();
                release.Wait();
                return new CursorWindowSnapshot(CursorWindowMode.Glass, "project");
            },
            _ => _folder,
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var read = reader.TryReadActiveFolderAsync(new IntPtr(42), cancellation.Token).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(1)));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        finally
        {
            release.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => reader.InFlightReadCount == 0,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task TimeoutDoesNotStartSecondReadForSameWindowAndRecovers()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var reader = new CursorWorkspaceReader(
            _ =>
            {
                Interlocked.Increment(ref calls);
                started.Set();
                release.Wait();
                return new CursorWindowSnapshot(CursorWindowMode.Glass, "project");
            },
            _ => _folder,
            TimeSpan.FromMilliseconds(50));
        var window = new IntPtr(42);

        Assert.Null(await reader.TryReadActiveFolderAsync(window, CancellationToken.None));
        Assert.True(started.IsSet);
        var retry = reader.TryReadActiveFolderAsync(window, CancellationToken.None);
        Assert.True(retry.IsCompletedSuccessfully);
        Assert.Null(await retry);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.Set();
        string? recovered = null;
        for (var attempt = 0; attempt < 100 && recovered is null; attempt++)
        {
            await Task.Delay(10);
            recovered = await reader.TryReadActiveFolderAsync(window, CancellationToken.None);
        }

        Assert.Equal(_folder, recovered);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task TimedOutReadRemovesItselfAfterLateCompletionWithoutAnotherRequest()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new CursorWorkspaceReader(
            _ =>
            {
                started.Set();
                release.Wait();
                return new CursorWindowSnapshot(CursorWindowMode.Glass, "project");
            },
            _ => _folder,
            TimeSpan.FromMilliseconds(50));

        Assert.Null(await reader.TryReadActiveFolderAsync(new IntPtr(42), CancellationToken.None));
        Assert.True(started.IsSet);
        Assert.Equal(1, reader.InFlightReadCount);

        release.Set();

        Assert.True(SpinWait.SpinUntil(
            () => reader.InFlightReadCount == 0,
            TimeSpan.FromSeconds(1)));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
