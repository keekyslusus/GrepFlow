using System.IO;
using System.Runtime.InteropServices;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class TotalCommanderWorkspaceReaderTests : IDisposable
{
    private readonly string _folder;

    public TotalCommanderWorkspaceReaderTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public async Task ReaderRequestsActivePanelForSuppliedWindow()
    {
        var window = new IntPtr(42);
        var observedWindow = IntPtr.Zero;
        string? observedCommand = null;
        var reader = new TotalCommanderWorkspaceReader(
            (candidate, command, _) =>
            {
                observedWindow = candidate;
                observedCommand = command;
                return ValueTask.FromResult<string?>(_folder);
            },
            Directory.Exists);

        var path = await reader.TryReadActivePanelPathAsync(window, CancellationToken.None);

        Assert.Equal(_folder, path);
        Assert.Equal(window, observedWindow);
        Assert.Equal("SP", observedCommand);
    }

    [Fact]
    public async Task NullProtocolResponseReturnsNull()
    {
        var reader = new TotalCommanderWorkspaceReader(
            (_, _, _) => ValueTask.FromResult<string?>(null),
            Directory.Exists);

        Assert.Null(await reader.TryReadActivePanelPathAsync(new IntPtr(42), CancellationToken.None));
    }

    [Fact]
    public void NormalizePathReturnsExistingLocalDirectory()
    {
        Assert.Equal(_folder, TotalCommanderWorkspaceReader.NormalizePath(_folder, Directory.Exists));
    }

    [Fact]
    public void NormalizePathConvertsSeparatorsAndRemovesNonRootTrailingSeparators()
    {
        var raw = $"  {_folder.Replace('\\', '/')}///  ";

        Assert.Equal(_folder, TotalCommanderWorkspaceReader.NormalizePath(raw, Directory.Exists));
    }

    [Fact]
    public void NormalizePathPreservesDriveRoot()
    {
        var root = Path.GetPathRoot(_folder)!;

        Assert.Equal(root, TotalCommanderWorkspaceReader.NormalizePath(root, Directory.Exists));
    }

    [Theory]
    [InlineData(@"\\server\share\folder\", @"\\server\share\folder")]
    [InlineData(@"\\server\share\", @"\\server\share\")]
    public void NormalizePathHandlesUncDirectoriesWithoutLiveShare(string raw, string expected)
    {
        Assert.Equal(
            expected,
            TotalCommanderWorkspaceReader.NormalizePath(raw, candidate => candidate == raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative")]
    [InlineData(@"C:\missing")]
    [InlineData("ftp://example.com/folder")]
    [InlineData(@"\\\Secure FTP\example.com")]
    [InlineData(@"C:\archive.zip\inside")]
    public void NormalizePathRejectsUnsupportedOrMissingLocations(string? raw)
    {
        Assert.Null(TotalCommanderWorkspaceReader.NormalizePath(raw, _ => false));
    }

    [Fact]
    public async Task CancellationBeforeQueryDoesNotInvokeIt()
    {
        var queryCalls = 0;
        var reader = new TotalCommanderWorkspaceReader(
            (_, _, _) =>
            {
                queryCalls++;
                return ValueTask.FromResult<string?>(_folder);
            },
            Directory.Exists);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.TryReadActivePanelPathAsync(new IntPtr(42), cancellation.Token).AsTask());
        Assert.Equal(0, queryCalls);
    }

    [Fact]
    public async Task CancellationAfterAsynchronousQueryIsObserved()
    {
        var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new TotalCommanderWorkspaceReader(
            (_, _, _) => new ValueTask<string?>(response.Task),
            Directory.Exists);
        using var cancellation = new CancellationTokenSource();

        var read = reader.TryReadActivePanelPathAsync(new IntPtr(42), cancellation.Token).AsTask();
        cancellation.Cancel();
        response.SetResult(_folder);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task QueuedNativeReadRevalidatesWindowIdentityOnSta()
    {
        using var dispatcher = new StaDispatcher();
        using var dispatcherBlocked = new ManualResetEventSlim();
        using var releaseDispatcher = new ManualResetEventSlim();
        var identity = "total-commander";
        string? observedIdentity = null;
        var sendCalls = 0;
        dispatcher.Post(() =>
        {
            dispatcherBlocked.Set();
            releaseDispatcher.Wait();
        });
        Assert.True(dispatcherBlocked.Wait(TimeSpan.FromSeconds(1)));
        var reader = new TotalCommanderWorkspaceReader(
            dispatcher,
            _ =>
            {
                observedIdentity = identity;
                return false;
            },
            Send,
            Directory.Exists);

        try
        {
            var read = reader.TryReadActivePanelPathAsync(new IntPtr(42), CancellationToken.None).AsTask();
            identity = "replacement-window";
            releaseDispatcher.Set();

            Assert.Null(await read);
            Assert.Equal("replacement-window", observedIdentity);
            Assert.Equal(0, sendCalls);
        }
        finally
        {
            releaseDispatcher.Set();
        }

        IntPtr Send(
            IntPtr window,
            uint message,
            IntPtr wParam,
            ref NativeMethods.COPYDATASTRUCT copyData,
            uint flags,
            uint timeout,
            out UIntPtr result)
        {
            sendCalls++;
            result = UIntPtr.Zero;
            return IntPtr.Zero;
        }
    }

    [Fact]
    public async Task NativeSendClearsStaleLastErrorImmediatelyBeforeCall()
    {
        using var dispatcher = new StaDispatcher();
        int? observedLastError = null;
        var reader = new TotalCommanderWorkspaceReader(
            dispatcher,
            _ =>
            {
                Marshal.SetLastPInvokeError(1460);
                return true;
            },
            Send,
            Directory.Exists);

        Assert.Null(await reader.TryReadActivePanelPathAsync(new IntPtr(42), CancellationToken.None));
        Assert.Equal(0, observedLastError);

        IntPtr Send(
            IntPtr window,
            uint message,
            IntPtr wParam,
            ref NativeMethods.COPYDATASTRUCT copyData,
            uint flags,
            uint timeout,
            out UIntPtr result)
        {
            observedLastError = Marshal.GetLastPInvokeError();
            Marshal.SetLastPInvokeError(0);
            result = UIntPtr.Zero;
            return IntPtr.Zero;
        }
    }

    [Fact]
    public void ProtocolIdentifiersAndAnsiCommandSizeMatchTotalCommanderContract()
    {
        Assert.Equal(0x5747UL, TotalCommanderWorkspaceReader.GwRequestIdentifier);
        Assert.Equal(0x5752UL, TotalCommanderWorkspaceReader.RwResponseIdentifier);
        Assert.Equal(3U, TotalCommanderWorkspaceReader.ActivePanelCommandByteCount);
        Assert.Equal(
            TotalCommanderWorkspaceReader.ActivePanelCommandByteCount,
            TotalCommanderWorkspaceReader.GetAnsiCommandByteCount(
                TotalCommanderWorkspaceReader.ActivePanelCommand));
    }

    [Fact]
    public void DecodeResponseAcceptsLengthsWithAndWithoutTerminator()
    {
        var sender = new IntPtr(42);
        var withoutTerminator = Marshal.StringToHGlobalUni(@"C:\workspace");
        var withTerminator = Marshal.StringToHGlobalUni(@"C:\workspace");
        try
        {
            Assert.Equal(
                @"C:\workspace",
                TotalCommanderWorkspaceReader.DecodeResponse(
                    TotalCommanderWorkspaceReader.RwResponseIdentifier,
                    sender,
                    sender,
                    (uint)(@"C:\workspace".Length * sizeof(char)),
                    withoutTerminator));
            Assert.Equal(
                @"C:\workspace",
                TotalCommanderWorkspaceReader.DecodeResponse(
                    TotalCommanderWorkspaceReader.RwResponseIdentifier,
                    sender,
                    sender,
                    (uint)((@"C:\workspace".Length + 1) * sizeof(char)),
                    withTerminator));
        }
        finally
        {
            Marshal.FreeHGlobal(withoutTerminator);
            Marshal.FreeHGlobal(withTerminator);
        }
    }

    [Fact]
    public void DecodeResponseStopsAtFirstNull()
    {
        var sender = new IntPtr(42);
        var data = Marshal.StringToHGlobalUni("C:\\workspace\0ignored");
        try
        {
            Assert.Equal(
                @"C:\workspace",
                TotalCommanderWorkspaceReader.DecodeResponse(
                    TotalCommanderWorkspaceReader.RwResponseIdentifier,
                    sender,
                    sender,
                    (uint)("C:\\workspace\0ignored".Length * sizeof(char)),
                    data));
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [Fact]
    public void DecodeResponseRejectsMalformedMetadata()
    {
        var sender = new IntPtr(42);
        var data = Marshal.StringToHGlobalUni(@"C:\workspace");
        try
        {
            Assert.Null(TotalCommanderWorkspaceReader.DecodeResponse(0, sender, sender, 2, data));
            Assert.Null(TotalCommanderWorkspaceReader.DecodeResponse(
                TotalCommanderWorkspaceReader.RwResponseIdentifier,
                new IntPtr(84),
                sender,
                2,
                data));
            Assert.Null(TotalCommanderWorkspaceReader.DecodeResponse(
                TotalCommanderWorkspaceReader.RwResponseIdentifier,
                sender,
                sender,
                2,
                IntPtr.Zero));
            Assert.Null(TotalCommanderWorkspaceReader.DecodeResponse(
                TotalCommanderWorkspaceReader.RwResponseIdentifier,
                sender,
                sender,
                0,
                data));
            Assert.Null(TotalCommanderWorkspaceReader.DecodeResponse(
                TotalCommanderWorkspaceReader.RwResponseIdentifier,
                sender,
                sender,
                3,
                data));
            Assert.Null(TotalCommanderWorkspaceReader.DecodeResponse(
                TotalCommanderWorkspaceReader.RwResponseIdentifier,
                sender,
                sender,
                TotalCommanderWorkspaceReader.MaximumResponseByteCount + 2,
                data));
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }
}
