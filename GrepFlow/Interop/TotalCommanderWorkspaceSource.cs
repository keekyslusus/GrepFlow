using System.Text;

namespace GrepFlow.Interop;

public sealed class TotalCommanderWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "total-commander";

    private const string ProcessImageName32 = "TOTALCMD.EXE";
    private const string ProcessImageName64 = "TOTALCMD64.EXE";
    private const string WindowClassName = "TTOTAL_CMD";

    private readonly Func<IntPtr, bool> _matchesTotalCommanderWindow;
    private readonly Func<IntPtr, CancellationToken, ValueTask<string?>> _readActivePanelPath;
    private IntPtr _window;

    public TotalCommanderWorkspaceSource(TotalCommanderWorkspaceReader reader)
        : this(IsTotalCommanderWindow, reader.TryReadActivePanelPathAsync)
    {
    }

    internal TotalCommanderWorkspaceSource(
        Func<IntPtr, bool> matchesTotalCommanderWindow,
        Func<IntPtr, CancellationToken, ValueTask<string?>> readActivePanelPath)
    {
        _matchesTotalCommanderWindow = matchesTotalCommanderWindow;
        _readActivePanelPath = readActivePanelPath;
    }

    public string Id => SourceId;

    public string DisplayName => "Total Commander";

    public bool MatchesForeground(IntPtr window)
    {
        if (!_matchesTotalCommanderWindow(window)) return false;

        Volatile.Write(ref _window, window);
        return true;
    }

    public async ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _window);
        if (window == IntPtr.Zero) return null;

        if (!_matchesTotalCommanderWindow(window))
        {
            Interlocked.CompareExchange(ref _window, IntPtr.Zero, window);
            return null;
        }

        var path = await _readActivePanelPath(window, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (path is null) return null;

        return new ActiveFolder(path, DisplayName, FromNearestWindow: false);
    }

    internal static bool IsTotalCommanderWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return false;

        var fileName = ForegroundProcess.TryGetImageFileName(window);
        if (!MatchesProcessImage(fileName)) return false;

        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(window, className, className.Capacity) <= 0) return false;

        return MatchesWindowIdentity(fileName, className.ToString());
    }

    internal static bool MatchesWindowIdentity(string? processImageName, string? windowClassName)
        => MatchesProcessImage(processImageName) &&
            string.Equals(windowClassName, WindowClassName, StringComparison.Ordinal);

    private static bool MatchesProcessImage(string? processImageName)
        => string.Equals(processImageName, ProcessImageName32, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processImageName, ProcessImageName64, StringComparison.OrdinalIgnoreCase);
}
