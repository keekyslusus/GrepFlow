namespace GrepFlow.Interop;

// never caches folder paths: navigating inside an already focused window raises no foreground event
public sealed class ForegroundWorkspaceTracker : IDisposable
{
    private readonly StaDispatcher _dispatcher;
    private readonly IReadOnlyList<IWorkspaceSource> _sources;
    private readonly ExplorerHwndCache _explorerHwnd;
    private readonly PluginLog? _log;

    // must stay reachable: the OS keeps only an unmanaged pointer to the hook callback
    private readonly WinEventProc _callback;

    private IntPtr _hook;
    private IntPtr _foregroundWindow;
    private string? _lastSourceId;
    private string? _lastMatchWarnFingerprint;

    public ForegroundWorkspaceTracker(
        StaDispatcher dispatcher,
        IReadOnlyList<IWorkspaceSource> sources,
        ExplorerHwndCache explorerHwnd,
        PluginLog? log = null)
    {
        _dispatcher = dispatcher;
        _sources = sources;
        _explorerHwnd = explorerHwnd;
        _log = log;
        _callback = OnForegroundChanged;
    }

    public string? LastSourceId => Volatile.Read(ref _lastSourceId);

    public void Start() => _dispatcher.Post(() =>
    {
        CaptureForeground(NativeMethods.GetForegroundWindow());
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    });

    private void OnForegroundChanged(
        IntPtr hookHandle,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND) return;
        CaptureForeground(window);
    }

    internal void CaptureForeground(IntPtr window)
    {
        if (window == IntPtr.Zero) return;

        var previous = _foregroundWindow;
        _foregroundWindow = window;
        if (previous != IntPtr.Zero && previous != window)
            TryCapture(previous);

        TryCapture(window);
    }

    private void TryCapture(IntPtr window)
    {
        if (ExplorerWindow.IsFolderWindow(window))
            _explorerHwnd.Capture(window);

        foreach (var source in _sources)
        {
            try
            {
                if (!source.MatchesForeground(window)) continue;
                Volatile.Write(ref _lastSourceId, source.Id);
                return;
            }
            catch (Exception exception)
            {
                WarnMatchOnce(source.Id, exception);
            }
        }
    }

    private void WarnMatchOnce(string sourceId, Exception exception)
    {
        var fingerprint = $"{sourceId}:{exception.GetType().Name}:{exception.Message}";
        if (string.Equals(_lastMatchWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;

        _lastMatchWarnFingerprint = fingerprint;
        _log?.Warn(
            nameof(ForegroundWorkspaceTracker),
            $"MatchesForeground failed for source '{sourceId}': {exception.GetType().Name}: {exception.Message}");
    }

    public void Dispose()
    {
        _dispatcher.Send(() =>
        {
            if (_hook == IntPtr.Zero) return;
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        });
    }
}
