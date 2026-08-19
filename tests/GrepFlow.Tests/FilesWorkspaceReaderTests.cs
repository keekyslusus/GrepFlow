using System.IO;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class FilesWorkspaceReaderTests : IDisposable
{
    private readonly string _folder;

    public FilesWorkspaceReaderTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public void NormalizePathReturnsExistingAbsoluteDirectory()
    {
        Assert.Equal(_folder, FilesWorkspaceReader.NormalizePath(_folder));
    }

    [Fact]
    public void NormalizePathConvertsSeparatorsAndRemovesTrailingSeparator()
    {
        var raw = _folder.Replace('\\', '/') + "/";

        Assert.Equal(_folder, FilesWorkspaceReader.NormalizePath(raw));
    }

    [Fact]
    public void NormalizePathRejectsVirtualAndMissingLocations()
    {
        Assert.Null(FilesWorkspaceReader.NormalizePath("Home"));
        Assert.Null(FilesWorkspaceReader.NormalizePath(Path.Combine(_folder, "missing")));
    }

    [Fact]
    public void NormalizePathPreservesDriveRoot()
    {
        var root = Path.GetPathRoot(_folder);

        Assert.Equal(root, FilesWorkspaceReader.NormalizePath(root));
    }

    [Fact]
    public async Task ReaderRequestsFilesIntegrationAutomationId()
    {
        var window = new IntPtr(42);
        IntPtr observedWindow = IntPtr.Zero;
        string? observedAutomationId = null;
        var reader = new FilesWorkspaceReader(
            _ => true,
            (candidate, automationId) =>
            {
                observedWindow = candidate;
                observedAutomationId = automationId;
                return _folder;
            },
            TimeSpan.FromSeconds(1));

        var path = await reader.TryReadCurrentPathAsync(window, CancellationToken.None);

        Assert.Equal(_folder, path);
        Assert.Equal(window, observedWindow);
        Assert.Equal("CurrentPathGet", observedAutomationId);
    }

    [Fact]
    public async Task UnavailableWindowDoesNotStartAutomationRead()
    {
        var readCalls = 0;
        var reader = new FilesWorkspaceReader(
            _ => false,
            (_, _) =>
            {
                readCalls++;
                return _folder;
            },
            TimeSpan.FromSeconds(1));

        var path = await reader.TryReadCurrentPathAsync(new IntPtr(42), CancellationToken.None);

        Assert.Null(path);
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task CancellationStopsWaitingForAutomationRead()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var completed = 0;
        var reader = new FilesWorkspaceReader(
            _ => true,
            (_, _) =>
            {
                started.Set();
                release.Wait();
                Volatile.Write(ref completed, 1);
                return _folder;
            },
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var read = reader.TryReadCurrentPathAsync(new IntPtr(42), cancellation.Token).AsTask();
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

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref completed) == 1, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task TimeoutReturnsNullWithoutStartingAnotherBlockedRead()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var readCalls = 0;
        var reader = new FilesWorkspaceReader(
            _ => true,
            (_, _) =>
            {
                Interlocked.Increment(ref readCalls);
                started.Set();
                release.Wait();
                return _folder;
            },
            TimeSpan.FromMilliseconds(50));
        var window = new IntPtr(42);

        Assert.Null(await reader.TryReadCurrentPathAsync(window, CancellationToken.None));
        Assert.True(started.IsSet);

        var retry = reader.TryReadCurrentPathAsync(window, CancellationToken.None);
        Assert.True(retry.IsCompletedSuccessfully);
        Assert.Null(await retry);

        var otherWindowRetry = reader.TryReadCurrentPathAsync(new IntPtr(84), CancellationToken.None);
        Assert.True(otherWindowRetry.IsCompletedSuccessfully);
        Assert.Null(await otherWindowRetry);
        Assert.Equal(1, Volatile.Read(ref readCalls));

        release.Set();
        string? recovered = null;
        for (var attempt = 0; attempt < 100 && recovered is null; attempt++)
        {
            await Task.Delay(10);
            recovered = await reader.TryReadCurrentPathAsync(new IntPtr(84), CancellationToken.None);
        }

        Assert.Equal(_folder, recovered);
        Assert.Equal(2, Volatile.Read(ref readCalls));
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }
}
