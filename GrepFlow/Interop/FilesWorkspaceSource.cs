using System.Text;

namespace GrepFlow.Interop;

public sealed class FilesWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "files";

    private const string ProcessImageName = "Files.exe";
    private const string WindowClassName = "WinUIDesktopWin32WindowClass";

    private readonly Func<IntPtr, bool> _matchesFilesWindow;
    private readonly Func<IntPtr, CancellationToken, ValueTask<string?>> _readCurrentPath;
    private IntPtr _window;

    public FilesWorkspaceSource(FilesWorkspaceReader reader)
        : this(IsFilesWindow, reader.TryReadCurrentPathAsync)
    {
    }

    internal FilesWorkspaceSource(
        Func<IntPtr, bool> matchesFilesWindow,
        Func<IntPtr, CancellationToken, ValueTask<string?>> readCurrentPath)
    {
        _matchesFilesWindow = matchesFilesWindow;
        _readCurrentPath = readCurrentPath;
    }

    public string Id => SourceId;

    public string DisplayName => "Files";

    public bool MatchesForeground(IntPtr window)
    {
        if (!_matchesFilesWindow(window)) return false;

        Volatile.Write(ref _window, window);
        return true;
    }

    public async ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _window);
        if (window == IntPtr.Zero) return null;

        if (!_matchesFilesWindow(window))
        {
            Interlocked.CompareExchange(ref _window, IntPtr.Zero, window);
            return null;
        }

        var path = await _readCurrentPath(window, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (path is null) return null;

        return new ActiveFolder(path, DisplayName, FromNearestWindow: false);
    }

    private static bool IsFilesWindow(IntPtr window)
    {
        var fileName = ForegroundProcess.TryGetImageFileName(window);
        if (!string.Equals(fileName, ProcessImageName, StringComparison.OrdinalIgnoreCase)) return false;

        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(window, className, className.Capacity) <= 0) return false;

        return MatchesWindowIdentity(fileName, className.ToString());
    }

    internal static bool MatchesWindowIdentity(string? processImageName, string? windowClassName)
        => string.Equals(processImageName, ProcessImageName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(windowClassName, WindowClassName, StringComparison.Ordinal);
}
