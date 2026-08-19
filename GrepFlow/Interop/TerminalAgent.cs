namespace GrepFlow.Interop;

public enum TerminalAgentKind
{
    Codex,
    ClaudeCode,
}

public enum TerminalHostKind
{
    DedicatedShell,
    Multiplexed,
}

public sealed record TerminalAgentProcess(uint ProcessId, TerminalAgentKind Kind);

public sealed record TerminalAgentWorkspace(TerminalAgentKind Kind, string WorkingDirectory);

public sealed record TerminalAgentAssociation(
    TerminalHostKind HostKind,
    IReadOnlyList<TerminalAgentProcess> Processes);

public static class TerminalAgentProfiles
{
    public static string ImageFileName(TerminalAgentKind kind)
        => kind switch
        {
            TerminalAgentKind.Codex => "codex.exe",
            TerminalAgentKind.ClaudeCode => "claude.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static string DisplayName(TerminalAgentKind kind)
        => kind switch
        {
            TerminalAgentKind.Codex => "Codex CLI",
            TerminalAgentKind.ClaudeCode => "Claude Code",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
