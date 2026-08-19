using System.Diagnostics;
using System.IO;
using GrepFlow.Search;

namespace GrepFlow.Interop;

public sealed class NotepadPlusPlusMatchLauncher : IAssociatedApplicationLauncher
{
    private const string NotepadPlusPlusExecutable = "notepad++.exe";

    private readonly IProcessStarter _processStarter;

    public NotepadPlusPlusMatchLauncher(IProcessStarter processStarter)
    {
        _processStarter = processStarter;
    }

    public bool Recognizes(string executablePath) =>
        Path.GetFileName(executablePath).Equals(NotepadPlusPlusExecutable, StringComparison.OrdinalIgnoreCase);

    public bool TryLaunch(string executablePath, RipgrepMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.AbsolutePath) || match.LineNumber <= 0) return false;

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add($"-n{match.LineNumber}");
            startInfo.ArgumentList.Add(match.AbsolutePath);
            _processStarter.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
