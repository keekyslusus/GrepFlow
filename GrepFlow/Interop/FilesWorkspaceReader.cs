using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace GrepFlow.Interop;

public sealed class FilesWorkspaceReader
{
    private const string CurrentPathAutomationId = "CurrentPathGet";
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMilliseconds(200);

    private readonly Func<IntPtr, bool> _windowExists;
    private readonly Func<IntPtr, string, string?> _readAutomationValue;
    private readonly TimeSpan _readTimeout;
    private readonly PluginLog? _log;
    private readonly object _gate = new();

    private Task<string?>? _inFlightRead;
    private IntPtr _inFlightWindow;
    private bool _inFlightTimedOut;
    private string? _lastWarnFingerprint;

    public FilesWorkspaceReader(PluginLog? log = null)
        : this(NativeMethods.IsWindow, ReadAutomationValue, DefaultReadTimeout, log)
    {
    }

    internal FilesWorkspaceReader(
        Func<IntPtr, bool> windowExists,
        Func<IntPtr, string, string?> readAutomationValue,
        TimeSpan readTimeout,
        PluginLog? log = null)
    {
        _windowExists = windowExists;
        _readAutomationValue = readAutomationValue;
        _readTimeout = readTimeout;
        _log = log;
    }

    public async ValueTask<string?> TryReadCurrentPathAsync(IntPtr window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (window == IntPtr.Zero || !_windowExists(window)) return null;

        Task<string?> read;
        lock (_gate)
        {
            ClearCompletedRead();

            if (_inFlightRead is not null)
            {
                if (_inFlightWindow != window || _inFlightTimedOut) return null;
                read = _inFlightRead;
            }
            else
            {
                _inFlightWindow = window;
                _inFlightTimedOut = false;
                read = _inFlightRead = Task.Run(() => ReadCurrentPath(window));
            }
        }

        try
        {
            return await read.WaitAsync(_readTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlightRead, read)) _inFlightTimedOut = true;
            }

            WarnOnce("timeout", $"reading the Files path exceeded {_readTimeout.TotalMilliseconds:0} ms");
            return null;
        }
        finally
        {
            if (read.IsCompleted)
            {
                lock (_gate) ClearCompletedRead();
            }
        }
    }

    private string? ReadCurrentPath(IntPtr window)
    {
        try
        {
            if (!_windowExists(window)) return null;
            return NormalizePath(_readAutomationValue(window, CurrentPathAutomationId));
        }
        catch (ElementNotAvailableException exception)
        {
            WarnOnce("unavailable", exception.Message);
            return null;
        }
        catch (COMException exception)
        {
            WarnOnce("com", exception.Message);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            WarnOnce("operation", exception.Message);
            return null;
        }
        catch (ArgumentException exception)
        {
            WarnOnce("argument", exception.Message);
            return null;
        }
    }

    private static string? ReadAutomationValue(IntPtr window, string automationId)
    {
        var root = AutomationElement.FromHandle(window);
        var element = root?.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.AutomationIdProperty,
                automationId));
        if (element is null ||
            !element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
            pattern is not ValuePattern valuePattern)
            return null;

        return valuePattern.Current.Value;
    }

    internal static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var path = raw.Replace('/', '\\').Trim();
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return null;

        var root = Path.GetPathRoot(path);
        if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            path = path.TrimEnd('\\');

        return path;
    }

    private void WarnOnce(string kind, string message)
    {
        var fingerprint = $"{kind}:{message}";
        lock (_gate)
        {
            if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;
            _lastWarnFingerprint = fingerprint;
        }

        _log?.Warn(nameof(FilesWorkspaceReader), message);
    }

    private void ClearCompletedRead()
    {
        if (_inFlightRead is not { IsCompleted: true }) return;

        _ = _inFlightRead.Exception;
        _inFlightRead = null;
        _inFlightWindow = IntPtr.Zero;
        _inFlightTimedOut = false;
    }
}
