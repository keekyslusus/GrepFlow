namespace GrepFlow.Interop;

public sealed class VisualStudioCodeWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "vscode";

    private const string ProcessImageName = "Code.exe";

    private readonly Func<IntPtr, bool> _matchesVisualStudioCodeWindow;
    private readonly Func<string?> _readLastActiveFolder;
    private IntPtr _window;

    public VisualStudioCodeWorkspaceSource(VisualStudioCodeSessionReader reader)
        : this(IsVisualStudioCodeWindow, reader.TryReadLastActiveFolder)
    {
    }

    internal VisualStudioCodeWorkspaceSource(
        Func<IntPtr, bool> matchesVisualStudioCodeWindow,
        Func<string?> readLastActiveFolder)
    {
        _matchesVisualStudioCodeWindow = matchesVisualStudioCodeWindow;
        _readLastActiveFolder = readLastActiveFolder;
    }

    public string Id => SourceId;

    public string DisplayName => "Visual Studio Code";

    public bool MatchesForeground(IntPtr window)
    {
        if (!_matchesVisualStudioCodeWindow(window)) return false;

        Volatile.Write(ref _window, window);
        return true;
    }

    public ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _window);
        if (window == IntPtr.Zero) return ValueTask.FromResult<ActiveFolder?>(null);

        if (!_matchesVisualStudioCodeWindow(window))
        {
            Interlocked.CompareExchange(ref _window, IntPtr.Zero, window);
            return ValueTask.FromResult<ActiveFolder?>(null);
        }

        var path = _readLastActiveFolder();
        if (path is null) return ValueTask.FromResult<ActiveFolder?>(null);

        return ValueTask.FromResult<ActiveFolder?>(
            new ActiveFolder(path, DisplayName, FromNearestWindow: false));
    }

    private static bool IsVisualStudioCodeWindow(IntPtr window)
    {
        var fileName = ForegroundProcess.TryGetImageFileName(window);
        return string.Equals(fileName, ProcessImageName, StringComparison.OrdinalIgnoreCase);
    }
}
