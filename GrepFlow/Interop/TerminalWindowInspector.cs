using System.Text;

namespace GrepFlow.Interop;

public sealed record TerminalWindow(
    uint ProcessId,
    string ImageFileName,
    string Title);

public sealed class TerminalWindowInspector
{
    private static readonly HashSet<string> SupportedImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe",
        "conhost.exe",
        "cmd.exe",
    };

    public TerminalWindow? TryInspect(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;

        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return null;

            var imageFileName = ForegroundProcess.TryGetImageFileName(window);
            if (imageFileName is null || !SupportedImages.Contains(imageFileName)) return null;

            var titleLength = NativeMethods.GetWindowTextLength(window);
            var title = new StringBuilder(Math.Max(titleLength + 1, 1));
            NativeMethods.GetWindowText(window, title, title.Capacity);
            return new TerminalWindow(processId, imageFileName, title.ToString());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
