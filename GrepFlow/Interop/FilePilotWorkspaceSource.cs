namespace GrepFlow.Interop;

public sealed class FilePilotWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "filepilot";

    private const string ProcessImageName = "FPilot.exe";

    private readonly Func<IntPtr, bool> _matchesFilePilotWindow;
    private readonly Func<string?> _readSelectedPanelPath;
    private IntPtr _window;

    public FilePilotWorkspaceSource(FilePilotSessionReader reader)
        : this(IsFilePilotWindow, reader.TryReadSelectedPanelPath)
    {
    }

    public FilePilotWorkspaceSource(
        Func<IntPtr, bool> matchesFilePilotWindow,
        Func<string?> readSelectedPanelPath)
    {
        _matchesFilePilotWindow = matchesFilePilotWindow;
        _readSelectedPanelPath = readSelectedPanelPath;
    }

    public string Id => SourceId;

    public string DisplayName => "FilePilot";

    public bool MatchesForeground(IntPtr window)
    {
        if (!_matchesFilePilotWindow(window)) return false;

        Volatile.Write(ref _window, window);
        return true;
    }

    public ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _window);
        if (window == IntPtr.Zero) return ValueTask.FromResult<ActiveFolder?>(null);

        if (!_matchesFilePilotWindow(window))
        {
            Interlocked.CompareExchange(ref _window, IntPtr.Zero, window);
            return ValueTask.FromResult<ActiveFolder?>(null);
        }

        var path = _readSelectedPanelPath();
        if (path is null) return ValueTask.FromResult<ActiveFolder?>(null);

        return ValueTask.FromResult<ActiveFolder?>(
            new ActiveFolder(path, DisplayName, FromNearestWindow: false));
    }

    private static bool IsFilePilotWindow(IntPtr window)
    {
        var fileName = ForegroundProcess.TryGetImageFileName(window);
        return string.Equals(fileName, ProcessImageName, StringComparison.OrdinalIgnoreCase);
    }
}
