using System.IO;
using System.Text;

namespace GrepFlow.Interop;

internal sealed record ZedWindowSnapshot(
    IntPtr Window,
    uint ProcessId,
    string ImagePath,
    string Title,
    string ClassName);

internal sealed class ZedWindowInspector
{
    private const string ProcessImageName = "Zed.exe";
    private const string WindowClassName = "Zed::Window";

    private readonly Func<IntPtr, bool> _isWindow;
    private readonly Func<IntPtr, IntPtr> _getOwner;
    private readonly Func<IntPtr, string?> _getImagePath;
    private readonly Func<IntPtr, uint> _getProcessId;
    private readonly Func<IntPtr, string> _getClassName;
    private readonly Func<IntPtr, string> _getTitle;

    public ZedWindowInspector()
        : this(
            NativeMethods.IsWindow,
            window => NativeMethods.GetWindow(window, NativeMethods.GW_OWNER),
            ForegroundProcess.TryGetImagePath,
            GetProcessId,
            ReadClassName,
            ReadTitle)
    {
    }

    internal ZedWindowInspector(
        Func<IntPtr, bool> isWindow,
        Func<IntPtr, IntPtr> getOwner,
        Func<IntPtr, string?> getImagePath,
        Func<IntPtr, uint> getProcessId,
        Func<IntPtr, string> getClassName,
        Func<IntPtr, string> getTitle)
    {
        _isWindow = isWindow;
        _getOwner = getOwner;
        _getImagePath = getImagePath;
        _getProcessId = getProcessId;
        _getClassName = getClassName;
        _getTitle = getTitle;
    }

    public ZedWindowSnapshot? TryInspect(IntPtr window)
    {
        if (window == IntPtr.Zero || !_isWindow(window)) return null;
        if (_getOwner(window) != IntPtr.Zero) return null;

        var imagePath = _getImagePath(window);
        if (!MatchesImageName(imagePath)) return null;

        var className = _getClassName(window);
        if (!MatchesClassName(className)) return null;

        var processId = _getProcessId(window);
        if (processId == 0) return null;

        return new ZedWindowSnapshot(
            window,
            processId,
            imagePath!,
            _getTitle(window),
            className);
    }

    internal static bool MatchesImageName(string? imagePath)
        => string.Equals(
            imagePath is null ? null : Path.GetFileName(imagePath),
            ProcessImageName,
            StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesClassName(string? className)
        => string.Equals(className, WindowClassName, StringComparison.Ordinal);

    private static uint GetProcessId(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    private static string ReadClassName(IntPtr window)
    {
        var className = new StringBuilder(256);
        return NativeMethods.GetClassName(window, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    private static string ReadTitle(IntPtr window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0) return string.Empty;

        var title = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(window, title, title.Capacity) > 0
            ? title.ToString()
            : string.Empty;
    }
}
