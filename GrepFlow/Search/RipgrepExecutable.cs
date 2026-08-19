namespace GrepFlow.Search;

public sealed class RipgrepExecutable
{
    public RipgrepExecutable(string? path) => Path = path;

    public string? Path { get; private set; }

    public bool IsAvailable => !string.IsNullOrEmpty(Path);

    public void Set(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }
}
