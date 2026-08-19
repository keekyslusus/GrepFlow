namespace GrepFlow.Interop;

public sealed class VisualStudioWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "visualstudio";

    private const string ProcessImageName = "devenv.exe";

    private readonly Func<IntPtr, bool> _matchesVisualStudioWindow;
    private readonly Func<IntPtr, CancellationToken, ValueTask<string?>> _readWorkspace;
    private IntPtr _window;

    public VisualStudioWorkspaceSource(StaDispatcher dispatcher, VisualStudioDteWorkspaceReader reader)
        : this(
            IsVisualStudioWindow,
            async (window, token) =>
                await dispatcher.InvokeAsync(() => reader.TryReadWorkspace(window), token).ConfigureAwait(false))
    {
    }

    public VisualStudioWorkspaceSource(
        Func<IntPtr, bool> matchesVisualStudioWindow,
        Func<IntPtr, CancellationToken, ValueTask<string?>> readWorkspace)
    {
        _matchesVisualStudioWindow = matchesVisualStudioWindow;
        _readWorkspace = readWorkspace;
    }

    public string Id => SourceId;

    public string DisplayName => "Visual Studio";

    public bool MatchesForeground(IntPtr window)
    {
        if (!_matchesVisualStudioWindow(window)) return false;

        Volatile.Write(ref _window, window);
        return true;
    }

    public async ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _window);
        if (window == IntPtr.Zero) return null;

        if (!_matchesVisualStudioWindow(window))
        {
            Interlocked.CompareExchange(ref _window, IntPtr.Zero, window);
            return null;
        }

        var path = await _readWorkspace(window, token).ConfigureAwait(false);
        return path is null ? null : new ActiveFolder(path, DisplayName, FromNearestWindow: false);
    }

    private static bool IsVisualStudioWindow(IntPtr window)
    {
        var fileName = ForegroundProcess.TryGetImageFileName(window);
        return string.Equals(fileName, ProcessImageName, StringComparison.OrdinalIgnoreCase);
    }
}
