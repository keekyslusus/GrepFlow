namespace GrepFlow.Search;

public interface ISearchEngine
{
    IAsyncEnumerable<RipgrepMatch> SearchAsync(SearchRequest request, CancellationToken token);
}
