using System.IO;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GrepFlow.Interop;

public sealed record WindowsProcessIdentity(
    uint ProcessId,
    string ImageFileName,
    ulong CreationFileTime);

public sealed class WindowsProcessIdentityReader
{
    public WindowsProcessIdentity? TryRead(uint processId)
    {
        if (processId == 0) return null;

        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);
        if (process == IntPtr.Zero) return null;

        try
        {
            var imagePath = new StringBuilder(1024);
            var length = (uint)imagePath.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(process, 0, imagePath, ref length) ||
                !NativeMethods.GetProcessTimes(process, out var created, out _, out _, out _))
                return null;

            var imageFileName = Path.GetFileName(imagePath.ToString());
            if (string.IsNullOrWhiteSpace(imageFileName)) return null;

            return new WindowsProcessIdentity(
                processId,
                imageFileName,
                ToUInt64(created));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static ulong ToUInt64(FILETIME value)
        => ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or ArgumentException or InvalidOperationException or NotSupportedException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException;
}
