using System.IO;

namespace GrepFlow.Interop;

public sealed class CursorWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "cursor";

    private const string ProcessImageName = "Cursor.exe";

    private readonly Func<IntPtr, bool> _matchesCursorWindow;
    private readonly Func<IntPtr, CancellationToken, ValueTask<string?>> _readActiveFolder;
    private IntPtr _window;

    public CursorWorkspaceSource(CursorWorkspaceReader reader)
        : this(IsCursorWindow, reader.TryReadActiveFolderAsync)
    {
    }

    internal CursorWorkspaceSource(
        Func<IntPtr, bool> matchesCursorWindow,
        Func<IntPtr, CancellationToken, ValueTask<string?>> readActiveFolder)
    {
        _matchesCursorWindow = matchesCursorWindow;
        _readActiveFolder = readActiveFolder;
    }

    public string Id => SourceId;

    public string DisplayName => "Cursor";

    public bool MatchesForeground(IntPtr window)
    {
        if (!_matchesCursorWindow(window)) return false;

        Volatile.Write(ref _window, window);
        return true;
    }

    public async ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _window);
        if (window == IntPtr.Zero) return null;

        if (!_matchesCursorWindow(window))
        {
            Interlocked.CompareExchange(ref _window, IntPtr.Zero, window);
            return null;
        }

        var path = await _readActiveFolder(window, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (path is null || !Directory.Exists(path)) return null;

        return new ActiveFolder(path, DisplayName, FromNearestWindow: false);
    }

    private static bool IsCursorWindow(IntPtr window)
        => string.Equals(
            ForegroundProcess.TryGetImageFileName(window),
            ProcessImageName,
            StringComparison.OrdinalIgnoreCase);
}
