using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GrepFlow.Interop;

public interface IProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}

public sealed class ProcessStarter : IProcessStarter
{
    public void Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
    }
}

public interface IFileExistence
{
    bool Exists(string path);
}

public sealed class FileExistence : IFileExistence
{
    public bool Exists(string path) => File.Exists(path);
}

public interface IFileOpener
{
    void Open(string path);
}

public interface IExecutableFileOpener
{
    bool TryOpen(string executablePath, string filePath);
}

public sealed class ExecutableFileOpener : IExecutableFileOpener
{
    private readonly IProcessStarter _processStarter;

    public ExecutableFileOpener(IProcessStarter processStarter)
    {
        _processStarter = processStarter;
    }

    public bool TryOpen(string executablePath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(filePath);
            _processStarter.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ShellFileOpener : IFileOpener
{
    private readonly IProcessStarter _processStarter;

    public ShellFileOpener(IProcessStarter processStarter)
    {
        _processStarter = processStarter;
    }

    public void Open(string path) =>
        _processStarter.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
}

public interface IOpenWithDialogNative
{
    int Show(string path);
}

public sealed class WindowsOpenWithDialogNative : IOpenWithDialogNative
{
    public int Show(string path)
    {
        var info = new OpenAsInfo
        {
            File = path,
            Flags = OpenAsInfoFlags.AllowRegistration | OpenAsInfoFlags.Execute,
        };

        return SHOpenWithDialog(0, ref info);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(nint parentWindow, ref OpenAsInfo openAsInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string File;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Class;

        public OpenAsInfoFlags Flags;
    }

    [Flags]
    private enum OpenAsInfoFlags
    {
        AllowRegistration = 0x1,
        Execute = 0x4,
    }
}

public sealed class OpenWithDialogFileOpener : IFileOpener
{
    private readonly IOpenWithDialogNative _dialog;

    public OpenWithDialogFileOpener(IOpenWithDialogNative dialog)
    {
        _dialog = dialog;
    }

    public void Open(string path)
    {
        var result = _dialog.Show(path);
        if (result >= 0) return;

        throw new InvalidOperationException(
            $"Could not show the Open With dialog for {path}.",
            Marshal.GetExceptionForHR(result));
    }
}

public interface ISafeFileOpener
{
    SafeFileOpenOutcome OpenSafe(string path);
}

public enum SafeFileOpenOutcome
{
    Opened,
    OpenWithShown,
}

public sealed class SafeFileOpener : ISafeFileOpener
{
    private const int NoAssociationError = 1155;

    private readonly IFileOpener _defaultOpener;
    private readonly IFileOpener _openWithDialog;

    public SafeFileOpener(IFileOpener defaultOpener, IFileOpener openWithDialog)
    {
        _defaultOpener = defaultOpener;
        _openWithDialog = openWithDialog;
    }

    public SafeFileOpenOutcome OpenSafe(string path)
    {
        try
        {
            _defaultOpener.Open(path);
            return SafeFileOpenOutcome.Opened;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == NoAssociationError)
        {
            _openWithDialog.Open(path);
            return SafeFileOpenOutcome.OpenWithShown;
        }
    }
}

public sealed class NotepadFileOpener : IFileOpener
{
    private readonly IProcessStarter _processStarter;

    public NotepadFileOpener(IProcessStarter processStarter)
    {
        _processStarter = processStarter;
    }

    public void Open(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "notepad.exe"),
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(path);
        _processStarter.Start(startInfo);
    }
}
