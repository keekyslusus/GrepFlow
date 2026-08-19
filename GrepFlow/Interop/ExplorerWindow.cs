using System.Text;

namespace GrepFlow.Interop;

internal static class ExplorerWindow
{
    private const string FolderWindowClass = "CabinetWClass";
    private const string DesktopWindowClass = "ExploreWClass";
    private const int ClassNameCapacity = 256;

    public static bool IsFolderWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;

        var buffer = new StringBuilder(ClassNameCapacity);
        var length = NativeMethods.GetClassName(window, buffer, buffer.Capacity);
        if (length == 0) return false;

        var className = buffer.ToString();
        return string.Equals(className, FolderWindowClass, StringComparison.Ordinal)
            || string.Equals(className, DesktopWindowClass, StringComparison.Ordinal);
    }
}
