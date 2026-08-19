using System.Diagnostics;
using System.IO;

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

public sealed class OpenWithFileOpener : IFileOpener
{
    private readonly IProcessStarter _processStarter;

    public OpenWithFileOpener(IProcessStarter processStarter)
    {
        _processStarter = processStarter;
    }

    public void Open(string path) =>
        _processStarter.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "openas",
        });
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
