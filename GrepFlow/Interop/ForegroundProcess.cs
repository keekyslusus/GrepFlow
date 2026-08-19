using System.IO;
using System.Text;

namespace GrepFlow.Interop;

internal static class ForegroundProcess
{
    private const int ProcessImageCapacity = 1024;

    public static string? TryGetImageFileName(IntPtr window)
    {
        var path = TryGetImagePath(window);
        return path is null ? null : Path.GetFileName(path);
    }

    public static string? TryGetImagePath(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;

        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return null;

            var handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                inheritHandle: false,
                processId);
            if (handle == IntPtr.Zero) return null;

            try
            {
                var buffer = new StringBuilder(ProcessImageCapacity);
                var size = (uint)buffer.Capacity;
                if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
                return buffer.ToString();
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
