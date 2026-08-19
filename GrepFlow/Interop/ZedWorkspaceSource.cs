using System.IO;

namespace GrepFlow.Interop;

internal sealed class ZedWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "zed";

    private readonly Func<IntPtr, ZedWindowSnapshot?> _inspectWindow;
    private readonly Func<string, string?> _readActiveFolder;
    private readonly Func<string, bool> _directoryExists;
    private readonly object _gate = new();
    private WindowAssociation? _lastWindow;

    public ZedWorkspaceSource(ZedWindowInspector inspector, ZedStateReader stateReader)
        : this(inspector.TryInspect, stateReader.TryReadActiveFolder, Directory.Exists)
    {
    }

    internal ZedWorkspaceSource(
        Func<IntPtr, ZedWindowSnapshot?> inspectWindow,
        Func<string, string?> readActiveFolder,
        Func<string, bool>? directoryExists = null)
    {
        _inspectWindow = inspectWindow;
        _readActiveFolder = readActiveFolder;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public string Id => SourceId;

    public string DisplayName => "Zed";

    public bool MatchesForeground(IntPtr window)
    {
        var snapshot = _inspectWindow(window);
        if (snapshot is null) return false;

        lock (_gate) _lastWindow = new WindowAssociation(snapshot);
        return true;
    }

    public ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        WindowAssociation? association;
        lock (_gate) association = _lastWindow;
        if (association is null) return ValueTask.FromResult<ActiveFolder?>(null);

        var cached = association.Snapshot;
        var refreshed = _inspectWindow(cached.Window);
        if (refreshed is null || refreshed.ProcessId != cached.ProcessId)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_lastWindow, association)) _lastWindow = null;
            }

            return ValueTask.FromResult<ActiveFolder?>(null);
        }

        token.ThrowIfCancellationRequested();
        var path = _readActiveFolder(refreshed.Title);
        token.ThrowIfCancellationRequested();
        if (path is null || !_directoryExists(path))
            return ValueTask.FromResult<ActiveFolder?>(null);

        return ValueTask.FromResult<ActiveFolder?>(
            new ActiveFolder(path, DisplayName, FromNearestWindow: false));
    }

    private sealed record WindowAssociation(ZedWindowSnapshot Snapshot);
}
