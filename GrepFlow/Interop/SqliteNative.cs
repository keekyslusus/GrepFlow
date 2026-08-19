using System.Runtime.InteropServices;
using System.Text;

namespace GrepFlow.Interop;

internal static class SqliteNative
{
    public const int Ok = 0;
    public const int Row = 100;
    public const int Done = 101;
    public const int OpenReadonly = 0x00000001;
    public const int BusyTimeoutMilliseconds = 50;
    public static readonly IntPtr Transient = new(-1);

    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');

    public static string ErrorMessage(IntPtr database, int result)
    {
        if (database == IntPtr.Zero) return $"SQLite returned error {result}";
        var pointer = sqlite3_errmsg(database);
        return Marshal.PtrToStringUTF8(pointer) ?? $"SQLite returned error {result}";
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_open_v2(
        byte[] filename,
        out IntPtr database,
        int flags,
        IntPtr virtualFileSystem);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_prepare_v2(
        IntPtr database,
        byte[] sql,
        int byteCount,
        out IntPtr statement,
        IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_bind_text(
        IntPtr statement,
        int index,
        byte[] value,
        int byteCount,
        IntPtr destructor);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_column_bytes(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int sqlite3_close_v2(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern IntPtr sqlite3_errmsg(IntPtr database);
}
