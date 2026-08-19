using System.Diagnostics;
using System.IO;
using GrepFlow.Search;

namespace GrepFlow.Interop;

public interface IAssociatedApplicationLauncher
{
    bool Recognizes(string executablePath);

    bool TryLaunch(string executablePath, RipgrepMatch match);
}

public sealed class SublimeMatchLauncher : IAssociatedApplicationLauncher
{
    private const string SublimeExecutable = "sublime_text.exe";
    private const string SublimeCli = "subl.exe";

    private readonly IFileExistence _files;
    private readonly IProcessStarter _processStarter;

    public SublimeMatchLauncher(IFileExistence files, IProcessStarter processStarter)
    {
        _files = files;
        _processStarter = processStarter;
    }

    public bool Recognizes(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        return fileName.Equals(SublimeExecutable, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(SublimeCli, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryLaunch(string executablePath, RipgrepMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.AbsolutePath) || match.LineNumber <= 0) return false;

        try
        {
            var cli = ResolveCli(executablePath);
            if (cli is null) return false;

            var startInfo = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add($"{match.AbsolutePath}:{match.LineNumber}");
            _processStarter.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveCli(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        if (fileName.Equals(SublimeCli, StringComparison.OrdinalIgnoreCase)) return executablePath;
        if (!fileName.Equals(SublimeExecutable, StringComparison.OrdinalIgnoreCase)) return null;

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory)) return null;

        var cli = Path.Combine(directory, SublimeCli);
        return _files.Exists(cli) ? cli : null;
    }
}
