using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace GrepFlow.Interop;

internal delegate IntPtr TotalCommanderMessageSender(
    IntPtr window,
    uint message,
    IntPtr wParam,
    ref NativeMethods.COPYDATASTRUCT copyData,
    uint flags,
    uint timeout,
    out UIntPtr result);

public sealed class TotalCommanderWorkspaceReader
{
    internal const string ActivePanelCommand = "SP";
    internal const ulong GwRequestIdentifier = 0x5747;
    internal const ulong RwResponseIdentifier = 0x5752;
    internal const uint ActivePanelCommandByteCount = 3;
    internal const uint MaximumResponseByteCount = 64 * 1024;

    private const uint NativeTimeoutMilliseconds = 200;
    private const int ErrorTimeout = 1460;

    private readonly StaDispatcher? _dispatcher;
    private readonly Func<IntPtr, string, CancellationToken, ValueTask<string?>>? _query;
    private readonly Func<IntPtr, bool>? _matchesTotalCommanderWindow;
    private readonly TotalCommanderMessageSender? _sendMessageTimeout;
    private readonly Func<string, bool> _directoryExists;
    private readonly PluginLog? _log;
    private readonly object _warningGate = new();
    private string? _lastWarnFingerprint;

    public TotalCommanderWorkspaceReader(StaDispatcher dispatcher, PluginLog? log = null)
    {
        _dispatcher = dispatcher;
        _matchesTotalCommanderWindow = TotalCommanderWorkspaceSource.IsTotalCommanderWindow;
        _sendMessageTimeout = NativeMethods.SendMessageTimeoutW;
        _directoryExists = Directory.Exists;
        _log = log;
    }

    internal TotalCommanderWorkspaceReader(
        StaDispatcher dispatcher,
        Func<IntPtr, bool> matchesTotalCommanderWindow,
        TotalCommanderMessageSender sendMessageTimeout,
        Func<string, bool> directoryExists)
    {
        _dispatcher = dispatcher;
        _matchesTotalCommanderWindow = matchesTotalCommanderWindow;
        _sendMessageTimeout = sendMessageTimeout;
        _directoryExists = directoryExists;
    }

    internal TotalCommanderWorkspaceReader(
        Func<IntPtr, string, CancellationToken, ValueTask<string?>> query,
        Func<string, bool> directoryExists,
        PluginLog? log = null)
    {
        _query = query;
        _directoryExists = directoryExists;
        _log = log;
    }

    public async ValueTask<string?> TryReadActivePanelPathAsync(IntPtr window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        string? rawPath;
        if (_query is not null)
        {
            rawPath = await _query(window, ActivePanelCommand, token).ConfigureAwait(false);
        }
        else
        {
            rawPath = await _dispatcher!
                .InvokeAsync(() => QueryActivePanelPath(window, ActivePanelCommand, token), token)
                .ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        return NormalizePath(rawPath, _directoryExists);
    }

    internal static string? NormalizePath(string? raw, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var path = raw.Trim().Replace('/', '\\');
            if (!Path.IsPathFullyQualified(path) || !directoryExists(path)) return null;

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;

            var pathWithoutTrailingSeparators = path.TrimEnd('\\');
            var rootWithoutTrailingSeparators = root.TrimEnd('\\');
            return string.Equals(
                pathWithoutTrailingSeparators,
                rootWithoutTrailingSeparators,
                StringComparison.OrdinalIgnoreCase)
                ? root.EndsWith('\\') ? root : root + '\\'
                : pathWithoutTrailingSeparators;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    internal static string? DecodeResponse(
        ulong identifier,
        IntPtr sender,
        IntPtr expectedSender,
        uint byteCount,
        IntPtr data)
    {
        if (identifier != RwResponseIdentifier ||
            sender != expectedSender ||
            data == IntPtr.Zero ||
            byteCount == 0 ||
            (byteCount & 1) != 0 ||
            byteCount > MaximumResponseByteCount)
            return null;

        var value = Marshal.PtrToStringUni(data, checked((int)(byteCount / 2)));
        if (value is null) return null;

        var terminator = value.IndexOf('\0');
        return terminator < 0 ? value : value[..terminator];
    }

    internal static uint GetAnsiCommandByteCount(string command)
        => checked((uint)Encoding.ASCII.GetByteCount(command) + 1);

    private string? QueryActivePanelPath(IntPtr window, string command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (window == IntPtr.Zero) return null;

        HwndSource? receiver = null;
        HwndSourceHook? hook = null;
        IntPtr commandBuffer = IntPtr.Zero;
        string? response = null;

        try
        {
            receiver = new HwndSource(new HwndSourceParameters("GrepFlow Total Commander IPC")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            });

            hook = (IntPtr messageWindow, int message, IntPtr sender, IntPtr dataPointer, ref bool handled) =>
            {
                if (message != NativeMethods.WM_COPYDATA || dataPointer == IntPtr.Zero)
                    return IntPtr.Zero;

                var copyData = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(dataPointer);
                var decoded = DecodeResponse(
                    copyData.dwData.ToUInt64(),
                    sender,
                    window,
                    copyData.cbData,
                    copyData.lpData);
                if (decoded is null) return IntPtr.Zero;

                response = decoded;
                handled = true;
                return new IntPtr(1);
            };
            receiver.AddHook(hook);

            token.ThrowIfCancellationRequested();
            commandBuffer = Marshal.StringToHGlobalAnsi(command);
            var request = new NativeMethods.COPYDATASTRUCT
            {
                dwData = new UIntPtr(GwRequestIdentifier),
                cbData = GetAnsiCommandByteCount(command),
                lpData = commandBuffer,
            };

            if (!_matchesTotalCommanderWindow!(window)) return null;

            Marshal.SetLastPInvokeError(0);
            var sendResult = _sendMessageTimeout!(
                window,
                NativeMethods.WM_COPYDATA,
                receiver.Handle,
                ref request,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_ERRORONEXIT,
                NativeTimeoutMilliseconds,
                out _);
            if (sendResult == IntPtr.Zero)
            {
                var error = Marshal.GetLastPInvokeError();
                var kind = error == ErrorTimeout ? "timeout" : "send";
                var message = error == ErrorTimeout
                    ? $"Total Commander did not answer within {NativeTimeoutMilliseconds} ms"
                    : $"Total Commander request failed: {new Win32Exception(error).Message} ({error})";
                WarnOnce(kind, message);
                return null;
            }

            if (response is null)
                WarnOnce("protocol", "Total Commander returned no valid RW response");

            return response;
        }
        catch (ExternalException exception)
        {
            WarnOnce("interop", exception.Message);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            WarnOnce("operation", exception.Message);
            return null;
        }
        finally
        {
            if (commandBuffer != IntPtr.Zero) Marshal.FreeHGlobal(commandBuffer);
            if (receiver is not null && hook is not null) receiver.RemoveHook(hook);
            receiver?.Dispose();
        }
    }

    private void WarnOnce(string kind, string message)
    {
        var fingerprint = $"{kind}:{message}";
        lock (_warningGate)
        {
            if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;
            _lastWarnFingerprint = fingerprint;
        }

        _log?.Warn(nameof(TotalCommanderWorkspaceReader), message);
    }
}
