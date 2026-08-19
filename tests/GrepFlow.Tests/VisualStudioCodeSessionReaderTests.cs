using System.Text.Json;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class VisualStudioCodeSessionReaderTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-{Guid.NewGuid():N}");

    public VisualStudioCodeSessionReaderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void PercentEncodedFileUriReturnsLocalFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "folder with spaces")).FullName;
        var drive = folder[..2];
        var encodedDrive = $"{char.ToLowerInvariant(folder[0])}%3A";
        var uri = new Uri(folder).AbsoluteUri.Replace(drive, encodedDrive, StringComparison.OrdinalIgnoreCase);
        var reader = CreateReader(uri);

        var result = reader.TryReadLastActiveFolder();

        Assert.Equal(folder, result);
    }

    [Fact]
    public void MissingStateFileReturnsNull()
    {
        var reader = new VisualStudioCodeSessionReader(Path.Combine(_temporaryDirectory, "missing.json"));

        Assert.Null(reader.TryReadLastActiveFolder());
    }

    [Fact]
    public void InvalidJsonReturnsNull()
    {
        var statePath = StatePath();
        File.WriteAllText(statePath, "{");

        Assert.Null(new VisualStudioCodeSessionReader(statePath).TryReadLastActiveFolder());
    }

    [Fact]
    public void MissingLastActiveWindowReturnsNull()
    {
        var statePath = StatePath();
        File.WriteAllText(statePath, "{\"windowsState\":{}}");

        Assert.Null(new VisualStudioCodeSessionReader(statePath).TryReadLastActiveFolder());
    }

    [Fact]
    public void NonexistentFolderReturnsNull()
    {
        var folder = Path.Combine(_temporaryDirectory, "does-not-exist");

        Assert.Null(CreateReader(new Uri(folder).AbsoluteUri).TryReadLastActiveFolder());
    }

    [Theory]
    [InlineData("vscode-remote://ssh-remote+server/home/project")]
    [InlineData("file://server/share/project")]
    [InlineData("relative/path")]
    public void NonLocalFileUriReturnsNull(string uri)
    {
        Assert.Null(CreateReader(uri).TryReadLastActiveFolder());
    }

    [Fact]
    public void RewrittenStateFileIsObserved()
    {
        var first = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "second")).FullName;
        var statePath = WriteState(new Uri(first).AbsoluteUri);
        var reader = new VisualStudioCodeSessionReader(statePath);

        Assert.Equal(first, reader.TryReadLastActiveFolder());

        WriteState(new Uri(second).AbsoluteUri);

        Assert.Equal(second, reader.TryReadLastActiveFolder());
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private VisualStudioCodeSessionReader CreateReader(string folderUri)
        => new(WriteState(folderUri));

    private string WriteState(string folderUri)
    {
        var json = JsonSerializer.Serialize(new
        {
            windowsState = new
            {
                lastActiveWindow = new { folder = folderUri },
            },
        });
        var statePath = StatePath();
        File.WriteAllText(statePath, json);
        return statePath;
    }

    private string StatePath() => Path.Combine(_temporaryDirectory, "storage.json");
}
