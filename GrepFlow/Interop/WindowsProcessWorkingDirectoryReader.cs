using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GrepFlow.Interop;

public sealed class WindowsProcessWorkingDirectoryReader
{
    private const int ProcessBasicInformation = 0;
    private const int PebProcessParametersOffsetX64 = 0x20;
    private const int ProcessParametersCurrentDirectoryOffsetX64 = 0x38;
    private const int UnicodeStringBufferOffsetX64 = 0x08;
    private const int UnicodeStringSizeX64 = 0x10;
    private const int PointerSizeX64 = 0x08;
    private const int MaxDirectoryBytes = 32 * 1024;

    public string? TryRead(uint processId)
    {
        if (processId == 0 || !Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
            return null;

        IntPtr process = IntPtr.Zero;
        try
        {
            process = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ,
                inheritHandle: false,
                processId);
            if (process == IntPtr.Zero || !IsNativeX64(process)) return null;

            var status = NativeMethods.NtQueryInformationProcess(
                process,
                ProcessBasicInformation,
                out var information,
                Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(),
                out _);
            if (status != 0 || information.PebBaseAddress == IntPtr.Zero) return null;

            // windows exposes no supported API for another process's current directory.
            var parameters = ReadPointer(
                process,
                IntPtr.Add(information.PebBaseAddress, PebProcessParametersOffsetX64));
            if (parameters == IntPtr.Zero) return null;

            var currentDirectory = ReadExact(
                process,
                IntPtr.Add(parameters, ProcessParametersCurrentDirectoryOffsetX64),
                UnicodeStringSizeX64);
            if (currentDirectory is null) return null;

            var length = BitConverter.ToUInt16(currentDirectory, 0);
            var maximumLength = BitConverter.ToUInt16(currentDirectory, sizeof(ushort));
            var bufferAddress = new IntPtr(BitConverter.ToInt64(currentDirectory, UnicodeStringBufferOffsetX64));
            if (length == 0 ||
                (length & 1) != 0 ||
                length > maximumLength ||
                length > MaxDirectoryBytes ||
                bufferAddress == IntPtr.Zero)
                return null;

            var buffer = ReadExact(process, bufferAddress, length);
            return buffer is null ? null : NormalizeLocalDirectory(Encoding.Unicode.GetString(buffer));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
        finally
        {
            if (process != IntPtr.Zero) NativeMethods.CloseHandle(process);
        }
    }

    public static string? NormalizeLocalDirectory(string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Path.IsPathFullyQualified(value) ||
                value.StartsWith("\\\\", StringComparison.Ordinal) ||
                !Directory.Exists(value))
                return null;

            var path = Path.GetFullPath(value);
            return path.Length > 3 ? Path.TrimEndingDirectorySeparator(path) : path;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    private static bool IsNativeX64(IntPtr process)
        => NativeMethods.IsWow64Process2(process, out var processMachine, out var nativeMachine) &&
           processMachine == NativeMethods.IMAGE_FILE_MACHINE_UNKNOWN &&
           nativeMachine == NativeMethods.IMAGE_FILE_MACHINE_AMD64;

    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var bytes = ReadExact(process, address, PointerSizeX64);
        return bytes is null ? IntPtr.Zero : new IntPtr(BitConverter.ToInt64(bytes, 0));
    }

    private static byte[]? ReadExact(IntPtr process, IntPtr address, int byteCount)
    {
        var buffer = new byte[byteCount];
        return NativeMethods.ReadProcessMemory(
                   process,
                   address,
                   buffer,
                   (nuint)byteCount,
                   out var bytesRead) &&
               bytesRead == (nuint)byteCount
            ? buffer
            : null;
    }

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or ArgumentException or InvalidOperationException or NotSupportedException or
            EntryPointNotFoundException or DllNotFoundException or BadImageFormatException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException;
}
