namespace GrepFlow.Interop;

public sealed class CompositeActiveFolderProvider : IActiveFolderProvider
{
    private readonly IReadOnlyList<IWorkspaceSource> _sources;
    private readonly ForegroundWorkspaceTracker _tracker;

    public CompositeActiveFolderProvider(
        IReadOnlyList<IWorkspaceSource> sources,
        ForegroundWorkspaceTracker tracker)
    {
        _sources = sources;
        _tracker = tracker;
    }

    public async ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        var lastId = _tracker.LastSourceId;

        var primary = lastId is not null
            ? FindById(lastId)
            : (_sources.Count > 0 ? _sources[0] : null);

        if (primary is not null)
        {
            var folder = await primary.GetActiveFolderAsync(token).ConfigureAwait(false);
            if (folder is not null) return folder;
        }

        var primaryId = primary?.Id;
        foreach (var source in _sources)
        {
            if (primaryId is not null && string.Equals(source.Id, primaryId, StringComparison.Ordinal))
                continue;

            var folder = await source.GetActiveFolderAsync(token).ConfigureAwait(false);
            if (folder is not null)
                return folder with { FromNearestWindow = true };
        }

        return null;
    }

    private IWorkspaceSource? FindById(string id)
    {
        foreach (var source in _sources)
        {
            if (string.Equals(source.Id, id, StringComparison.Ordinal)) return source;
        }

        return null;
    }
}
