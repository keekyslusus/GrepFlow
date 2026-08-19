namespace GrepFlow.Interop;

public sealed record ActiveFolder(string Path, string SourceName, bool FromNearestWindow);

public interface IActiveFolderProvider
{
    ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token);
}
