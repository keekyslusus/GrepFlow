using System.Diagnostics;

namespace GrepFlow.Interop;

public interface ITerminalLauncher
{
    void Open(string workingDirectory);
}

public sealed class TerminalLauncher : ITerminalLauncher
{
    private readonly IProcessStarter _processStarter;
    private readonly string _shellExecutable;

    public TerminalLauncher(IProcessStarter processStarter, string shellExecutable)
    {
        _processStarter = processStarter;
        _shellExecutable = shellExecutable;
    }

    public void Open(string workingDirectory) =>
        _processStarter.Start(new ProcessStartInfo
        {
            FileName = _shellExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        });
}
