using System.IO;
using System.Text;

namespace GrepFlow.Interop;

internal sealed record SublimeTextWindowBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

internal sealed record SublimeTextWindowSnapshot(
    IntPtr Window,
    uint ProcessId,
    string ImagePath,
    string Title,
    IntPtr Owner,
    SublimeTextWindowBounds? Bounds);

internal sealed class SublimeTextWindowInspector
{
    private const string ProcessImageName = "sublime_text.exe";

    public SublimeTextWindowSnapshot? TryInspect(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return null;

        var owner = NativeMethods.GetWindow(window, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero) return null;

        var imagePath = ForegroundProcess.TryGetImagePath(window);
        if (!MatchesImageName(imagePath)) return null;

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return null;

        var title = ReadTitle(window);
        var bounds = NativeMethods.GetWindowRect(window, out var rectangle)
            ? new SublimeTextWindowBounds(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom)
            : null;

        return new SublimeTextWindowSnapshot(window, processId, imagePath!, title, owner, bounds);
    }

    internal static bool MatchesImageName(string? imagePath)
        => string.Equals(
            imagePath is null ? null : Path.GetFileName(imagePath),
            ProcessImageName,
            StringComparison.OrdinalIgnoreCase);

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
