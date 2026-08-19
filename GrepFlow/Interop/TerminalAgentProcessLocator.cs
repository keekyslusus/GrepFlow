namespace GrepFlow.Interop;

public sealed class TerminalAgentProcessLocator
{
    private const string CommandShellImage = "cmd.exe";

    public TerminalAgentAssociation? Locate(
        TerminalWindow terminal,
        WindowsProcessSnapshot processes)
    {
        if (!processes.TryGetProcess(terminal.ProcessId, out var terminalProcess) ||
            terminalProcess is null ||
            !string.Equals(
                terminalProcess.ImageFileName,
                terminal.ImageFileName,
                StringComparison.OrdinalIgnoreCase))
            return null;

        TerminalHostKind hostKind;
        if (string.Equals(terminal.ImageFileName, "WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase))
        {
            hostKind = TerminalHostKind.Multiplexed;
        }
        else if (string.Equals(terminal.ImageFileName, CommandShellImage, StringComparison.OrdinalIgnoreCase))
        {
            hostKind = TerminalHostKind.DedicatedShell;
        }
        else if (string.Equals(terminal.ImageFileName, "conhost.exe", StringComparison.OrdinalIgnoreCase) &&
                 processes.TryGetProcess(terminalProcess.ParentProcessId, out var parent) &&
                 parent is not null &&
                 string.Equals(parent.ImageFileName, CommandShellImage, StringComparison.OrdinalIgnoreCase))
        {
            hostKind = TerminalHostKind.DedicatedShell;
        }
        else
        {
            return null;
        }

        var result = new List<TerminalAgentProcess>();
        var commandShells = hostKind == TerminalHostKind.DedicatedShell
            ? [string.Equals(terminal.ImageFileName, CommandShellImage, StringComparison.OrdinalIgnoreCase)
                ? terminal.ProcessId
                : terminalProcess.ParentProcessId]
            : processes.FindDescendantProcesses(terminal.ProcessId, CommandShellImage);
        foreach (var commandShell in commandShells)
        {
            AddDescendants(result, processes, commandShell, TerminalAgentKind.Codex);
            AddDescendants(result, processes, commandShell, TerminalAgentKind.ClaudeCode);
        }

        return new TerminalAgentAssociation(
            hostKind,
            result.Distinct().ToArray());
    }

    private static void AddDescendants(
        ICollection<TerminalAgentProcess> result,
        WindowsProcessSnapshot processes,
        uint branchRoot,
        TerminalAgentKind kind)
    {
        foreach (var processId in processes.FindDescendantProcesses(
                     branchRoot,
                     TerminalAgentProfiles.ImageFileName(kind)))
            result.Add(new TerminalAgentProcess(processId, kind));
    }
}
