using System.Diagnostics;
using System.IO;
using GrepFlow.Search;

namespace GrepFlow.Interop;

public sealed class VisualStudioCodeMatchLauncher : IAssociatedApplicationLauncher
{
    private const string VisualStudioCodeExecutable = "Code.exe";

    private readonly IProcessStarter _processStarter;

    public VisualStudioCodeMatchLauncher(IProcessStarter processStarter)
    {
        _processStarter = processStarter;
    }

    public bool Recognizes(string executablePath) =>
        Path.GetFileName(executablePath).Equals(VisualStudioCodeExecutable, StringComparison.OrdinalIgnoreCase);

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
            startInfo.ArgumentList.Add("--reuse-window");
            startInfo.ArgumentList.Add("--goto");
            startInfo.ArgumentList.Add($"{match.AbsolutePath}:{match.LineNumber}");
            _processStarter.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
