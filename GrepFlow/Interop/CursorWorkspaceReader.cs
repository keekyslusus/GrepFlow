namespace GrepFlow.Interop;

public sealed class CursorWorkspaceReader
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Func<IntPtr, CursorWindowSnapshot?> _inspectWindow;
    private readonly Func<CursorWindowSnapshot, string?> _readState;
    private readonly TimeSpan _readTimeout;
    private readonly PluginLog? _log;
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, InFlightRead> _inFlight = [];
    private string? _lastWarnFingerprint;

    public CursorWorkspaceReader(
        CursorWindowInspector inspector,
        CursorStateReader stateReader,
        PluginLog? log = null)
        : this(inspector.TryInspect, stateReader.TryReadActiveFolder, DefaultReadTimeout, log)
    {
    }

    internal CursorWorkspaceReader(
        Func<IntPtr, CursorWindowSnapshot?> inspectWindow,
        Func<CursorWindowSnapshot, string?> readState,
        TimeSpan readTimeout,
        PluginLog? log = null)
    {
        _inspectWindow = inspectWindow;
        _readState = readState;
        _readTimeout = readTimeout;
        _log = log;
    }

    public async ValueTask<string?> TryReadActiveFolderAsync(IntPtr window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (window == IntPtr.Zero) return null;

        InFlightRead entry;
        lock (_gate)
        {
            ClearCompletedRead(window);
            if (_inFlight.TryGetValue(window, out entry!))
            {
                if (entry.TimedOut) return null;
            }
            else
            {
                var task = Task.Run(() => Read(window));
                entry = new InFlightRead(task);
                _inFlight.Add(window, entry);
                _ = task.ContinueWith(
                    _ => CompleteRead(window, entry),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        try
        {
            var path = await entry.Task.WaitAsync(_readTimeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return path;
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(window, out var current) && ReferenceEquals(current, entry))
                    current.TimedOut = true;
            }

            WarnOnce("timeout", $"reading the Cursor workspace exceeded {_readTimeout.TotalMilliseconds:0} ms");
            return null;
        }
        finally
        {
            if (entry.Task.IsCompleted)
            {
                CompleteRead(window, entry);
            }
        }
    }

    private string? Read(IntPtr window)
    {
        var snapshot = _inspectWindow(window);
        return snapshot is null ? null : _readState(snapshot);
    }

    private void ClearCompletedRead(IntPtr window)
    {
        if (!_inFlight.TryGetValue(window, out var entry) || !entry.Task.IsCompleted) return;

        _ = entry.Task.Exception;
        _inFlight.Remove(window);
    }

    private void CompleteRead(IntPtr window, InFlightRead entry)
    {
        _ = entry.Task.Exception;
        lock (_gate)
        {
            if (_inFlight.TryGetValue(window, out var current) && ReferenceEquals(current, entry))
                _inFlight.Remove(window);
        }
    }

    internal int InFlightReadCount
    {
        get
        {
            lock (_gate) return _inFlight.Count;
        }
    }

    private void WarnOnce(string kind, string message)
    {
        var fingerprint = $"{kind}:{message}";
        lock (_gate)
        {
            if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;
            _lastWarnFingerprint = fingerprint;
        }

        _log?.Warn(nameof(CursorWorkspaceReader), message);
    }

    private sealed class InFlightRead(Task<string?> task)
    {
        public Task<string?> Task { get; } = task;

        public bool TimedOut { get; set; }
    }
}
