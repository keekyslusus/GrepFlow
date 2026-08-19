using System.ComponentModel;
using System.IO;
using GrepFlow.Interop;
using GrepFlow.Search;

namespace GrepFlow.Presentation;

public sealed class ResultActions
{
    private readonly ResultActionHost _host;
    private readonly ITextProvider _texts;
    private readonly PluginLog _log;
    private readonly IMatchOpener _matchOpener;
    private readonly ITerminalLauncher _terminalLauncher;

    public ResultActions(
        ResultActionHost host,
        ITextProvider texts,
        PluginLog log,
        IMatchOpener matchOpener,
        ITerminalLauncher terminalLauncher)
    {
        _host = host;
        _texts = texts;
        _log = log;
        _matchOpener = matchOpener;
        _terminalLauncher = terminalLauncher;
    }

    public bool OpenMatch(RipgrepMatch match)
    {
        try
        {
            var outcome = _matchOpener.Open(match);
            if (outcome == MatchOpenOutcome.Blocked)
            {
                RevealInExplorer(match.AbsolutePath);
                _host.ShowError(
                    _texts.Get(TextKeys.PluginGrepflowPluginName),
                    _texts.Get(TextKeys.PluginGrepflowBlockedFileOpen, match.AbsolutePath));
            }

            return true;
        }
        catch (Win32Exception exception)
        {
            return Report(exception, match.AbsolutePath);
        }
        catch (InvalidOperationException exception)
        {
            return Report(exception, match.AbsolutePath);
        }
    }

    public bool OpenFolder(string folder)
    {
        _host.OpenDirectory(folder, null);
        return true;
    }

    public bool OpenTerminal(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            _terminalLauncher.Open(directory);
            return true;
        }
        catch (Win32Exception exception)
        {
            return ReportTerminal(exception, directory);
        }
        catch (InvalidOperationException exception)
        {
            return ReportTerminal(exception, directory);
        }
    }

    public bool RevealInExplorer(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return false;

        _host.OpenDirectory(directory, path);
        return true;
    }

    public bool CopyToClipboard(string text)
    {
        _host.CopyToClipboard(text);
        return true;
    }

    private bool Report(Exception exception, string path)
    {
        _log.Error(nameof(ResultActions), $"could not open {path}", exception);
        _host.ShowError(_texts.Get(TextKeys.PluginGrepflowPluginName), exception.Message);
        return false;
    }

    private bool ReportTerminal(Exception exception, string directory)
    {
        _log.Error(nameof(ResultActions), $"could not open terminal in {directory}", exception);
        _host.ShowError(
            _texts.Get(TextKeys.PluginGrepflowPluginName),
            _texts.Get(TextKeys.PluginGrepflowOpenTerminalFailed, directory, exception.Message));
        return false;
    }
}
