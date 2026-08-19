namespace GrepFlow.Interop;

public interface IWorkspaceSource
{
    string Id { get; }

    string DisplayName { get; }

    bool MatchesForeground(IntPtr window);

    ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token);
}
