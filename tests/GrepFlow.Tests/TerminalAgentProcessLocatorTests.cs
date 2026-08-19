using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class TerminalAgentProcessLocatorTests
{
    [Fact]
    public void DirectCmdFindsCodexAndClaudeDescendants()
    {
        var result = Locate(
            new TerminalWindow(10, "cmd.exe", "shell"),
            Process(10, 1, "cmd.exe"),
            Process(20, 10, "codex.exe"),
            Process(30, 10, "claude.exe"));

        Assert.NotNull(result);
        Assert.Equal(TerminalHostKind.DedicatedShell, result.HostKind);
        Assert.Equal(
            [new TerminalAgentProcess(20, TerminalAgentKind.Codex),
             new TerminalAgentProcess(30, TerminalAgentKind.ClaudeCode)],
            result.Processes);
    }

    [Fact]
    public void ImageNamesAreCaseInsensitive()
    {
        var result = Locate(
            new TerminalWindow(10, "CMD.EXE", "shell"),
            Process(10, 1, "cmd.exe"),
            Process(20, 10, "CODEX.EXE"),
            Process(30, 10, "CLAUDE.EXE"));

        Assert.Equal(2, result?.Processes.Count);
    }

    [Fact]
    public void ValidConhostUsesParentCmdBranch()
    {
        var result = Locate(
            new TerminalWindow(20, "conhost.exe", "shell"),
            Process(10, 1, "cmd.exe"),
            Process(20, 10, "conhost.exe"),
            Process(30, 10, "codex.exe"));

        Assert.NotNull(result);
        Assert.Equal(TerminalHostKind.DedicatedShell, result.HostKind);
        Assert.Equal([new TerminalAgentProcess(30, TerminalAgentKind.Codex)], result.Processes);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void PowerShellHostsAreRejected(string shellImage)
    {
        var result = Locate(
            new TerminalWindow(10, shellImage, "shell"),
            Process(10, 1, shellImage),
            Process(20, 10, "codex.exe"));

        Assert.Null(result);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void ConhostWithPowerShellParentIsRejected(string shellImage)
    {
        var result = Locate(
            new TerminalWindow(20, "conhost.exe", "shell"),
            Process(10, 1, shellImage),
            Process(20, 10, "conhost.exe"),
            Process(30, 10, "codex.exe"));

        Assert.Null(result);
    }

    [Fact]
    public void ConhostWithMissingParentIsRejected()
    {
        var result = Locate(
            new TerminalWindow(20, "conhost.exe", "shell"),
            Process(20, 10, "conhost.exe"),
            Process(30, 10, "codex.exe"));

        Assert.Null(result);
    }

    [Fact]
    public void ConhostWithWrongParentIsRejected()
    {
        var result = Locate(
            new TerminalWindow(20, "conhost.exe", "shell"),
            Process(10, 1, "explorer.exe"),
            Process(20, 10, "conhost.exe"),
            Process(30, 10, "claude.exe"));

        Assert.Null(result);
    }

    [Fact]
    public void WindowsTerminalUsesCmdBranches()
    {
        var result = Locate(
            new TerminalWindow(10, "WindowsTerminal.exe", "terminal"),
            Process(10, 1, "WindowsTerminal.exe"),
            Process(20, 10, "cmd.exe"),
            Process(30, 20, "claude.exe"));

        Assert.Equal(TerminalHostKind.Multiplexed, result?.HostKind);
        Assert.Equal([new TerminalAgentProcess(30, TerminalAgentKind.ClaudeCode)], result?.Processes);
    }

    [Fact]
    public void WindowsTerminalIgnoresPowerShellBranches()
    {
        var result = Locate(
            new TerminalWindow(10, "WindowsTerminal.exe", "terminal"),
            Process(10, 1, "WindowsTerminal.exe"),
            Process(20, 10, "powershell.exe"),
            Process(30, 20, "codex.exe"),
            Process(40, 10, "pwsh.exe"),
            Process(50, 40, "claude.exe"));

        Assert.NotNull(result);
        Assert.Empty(result.Processes);
    }

    [Fact]
    public void WrongTerminalSnapshotIdentityIsRejected()
    {
        var result = Locate(
            new TerminalWindow(10, "cmd.exe", "shell"),
            Process(10, 1, "powershell.exe"),
            Process(20, 10, "codex.exe"));

        Assert.Null(result);
    }

    [Fact]
    public void UnrelatedAgentOutsideBranchIsIgnored()
    {
        var result = Locate(
            new TerminalWindow(10, "cmd.exe", "shell"),
            Process(10, 1, "cmd.exe"),
            Process(20, 1, "claude.exe"));

        Assert.NotNull(result);
        Assert.Empty(result.Processes);
    }

    [Fact]
    public void MixedDescendantsKeepCorrectKinds()
    {
        var result = Locate(
            new TerminalWindow(10, "WindowsTerminal.exe", "terminal"),
            Process(10, 1, "WindowsTerminal.exe"),
            Process(20, 10, "cmd.exe"),
            Process(30, 20, "claude.exe"),
            Process(40, 20, "codex.exe"));

        Assert.Contains(new TerminalAgentProcess(30, TerminalAgentKind.ClaudeCode), result!.Processes);
        Assert.Contains(new TerminalAgentProcess(40, TerminalAgentKind.Codex), result.Processes);
    }

    [Fact]
    public void DuplicateAndCyclicSnapshotTerminatesAndDeduplicates()
    {
        var result = Locate(
            new TerminalWindow(10, "cmd.exe", "shell"),
            Process(10, 20, "cmd.exe"),
            Process(20, 10, "host.exe"),
            Process(30, 20, "codex.exe"),
            Process(30, 20, "codex.exe"));

        Assert.Equal([new TerminalAgentProcess(30, TerminalAgentKind.Codex)], result?.Processes);
    }

    private static TerminalAgentAssociation? Locate(
        TerminalWindow terminal,
        params WindowsProcessInfo[] processes)
        => new TerminalAgentProcessLocator().Locate(terminal, new WindowsProcessSnapshot(processes));

    private static WindowsProcessInfo Process(uint id, uint parent, string image)
        => new(id, parent, image);
}
