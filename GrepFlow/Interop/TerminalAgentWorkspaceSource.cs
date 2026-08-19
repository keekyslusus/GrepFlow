using System.IO;

namespace GrepFlow.Interop;

public sealed class TerminalAgentWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "terminal-agent";

    private readonly Func<IntPtr, bool> _tryMatchForeground;
    private readonly Func<IntPtr, TerminalAgentWorkspace?> _resolveWorkspace;
    private IntPtr _terminalWindow;

    public TerminalAgentWorkspaceSource(TerminalAgentForegroundResolver resolver)
        : this(resolver.TryMatchForeground, resolver.TryResolve)
    {
    }

    public TerminalAgentWorkspaceSource(
        Func<IntPtr, bool> tryMatchForeground,
        Func<IntPtr, TerminalAgentWorkspace?> resolveWorkspace)
    {
        _tryMatchForeground = tryMatchForeground;
        _resolveWorkspace = resolveWorkspace;
    }

    public string Id => SourceId;

    public string DisplayName => "Terminal agent";

    public bool MatchesForeground(IntPtr window)
    {
        if (!_tryMatchForeground(window)) return false;

        Volatile.Write(ref _terminalWindow, window);
        return true;
    }

    public ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _terminalWindow);
        if (window == IntPtr.Zero) return ValueTask.FromResult<ActiveFolder?>(null);

        var workspace = _resolveWorkspace(window);
        if (workspace is null || !Directory.Exists(workspace.WorkingDirectory))
            return ValueTask.FromResult<ActiveFolder?>(null);

        return ValueTask.FromResult<ActiveFolder?>(new ActiveFolder(
            workspace.WorkingDirectory,
            TerminalAgentProfiles.DisplayName(workspace.Kind),
            FromNearestWindow: false));
    }
}
