using System.IO;
using System.Text;

namespace GrepFlow.Interop;

internal sealed class JetBrainsIdeWorkspaceReader
{
    private readonly JetBrainsIdeStateLocator _stateLocator;
    private readonly JetBrainsIdeLogReader _logReader;
    private readonly Func<IntPtr, string?> _readWindowTitle;
    private readonly string _userHome;

    internal JetBrainsIdeWorkspaceReader(
        JetBrainsIdeStateLocator stateLocator,
        JetBrainsIdeLogReader logReader,
        Func<IntPtr, string?>? readWindowTitle = null,
        string? userHome = null)
    {
        _stateLocator = stateLocator;
        _logReader = logReader;
        _readWindowTitle = readWindowTitle ?? TryReadWindowTitle;
        _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    internal string? TryReadProjectFolder(JetBrainsIdeProcessWindow process)
    {
        var title = _readWindowTitle(process.Window);
        if (string.IsNullOrWhiteSpace(title)) return null;

        var explicitPath = JetBrainsIdeProjectTitleParser.TryGetExplicitProjectPath(title, _userHome);
        if (explicitPath is not null)
            return Directory.Exists(explicitPath) ? explicitPath : null;

        var state = _stateLocator.TryLocate(process);
        return state is null ? null : _logReader.TryResolveProjectPath(state.LogPath, title);
    }

    private static string? TryReadWindowTitle(IntPtr window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0) return null;

        var title = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(window, title, title.Capacity) > 0
            ? title.ToString()
            : null;
    }
}
