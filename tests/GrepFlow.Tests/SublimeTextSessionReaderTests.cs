using System.Text.Json;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class SublimeTextSessionReaderTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-Sublime-{Guid.NewGuid():N}");

    public SublimeTextSessionReaderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void ParsesWindowsFoldersIdsAndOptionalNamesInOrder()
    {
        var first = CreateFolder("first");
        var second = CreateFolder("second");
        var third = CreateFolder("third");
        WriteRaw($$"""
            {
              // Sublime session files permit comments.
              "windows": [
                {
                  "window_id": 114,
                  "position": "0,0,1,-1,-1,-1,-1,1061,2506,228,4003,monitor",
                  "folders": [
                    { "path": {{JsonSerializer.Serialize(first)}} },
                    { "path": {{JsonSerializer.Serialize(second)}}, "name": "Second root" },
                  ],
                },
                {
                  "window_id": 111,
                  "folders": [{ "path": {{JsonSerializer.Serialize(third)}}}]
                },
              ],
            }
            """);

        var session = Read();

        Assert.NotNull(session);
        Assert.Equal([114L, 111L], session.Windows.Select(window => window.WindowId));
        Assert.Equal([first, second], session.Windows[0].Folders.Select(folder => folder.Path));
        Assert.Equal("Second root", session.Windows[0].Folders[1].Name);
        Assert.Equal(new SublimeTextWindowBounds(2506, 228, 4003, 1061), session.Windows[0].Bounds);
    }

    [Fact]
    public void MissingFileReturnsNoSession()
    {
        var reader = new SublimeTextSessionReader(Path.Combine(_temporaryDirectory, "missing.sublime_session"));

        Assert.Null(reader.TryReadSession());
    }

    [Fact]
    public void InvalidOrPartialJsonReturnsNoSession()
    {
        WriteRaw("{\"windows\":[");

        Assert.Null(Read());
    }

    [Fact]
    public void MissingOptionalPropertiesReturnsEmptyFoldersWithoutThrowing()
    {
        WriteRaw("{\"windows\":[{\"window_id\":7}]}");

        var session = Read();

        Assert.NotNull(session);
        Assert.Empty(session.Windows[0].Folders);
        Assert.Null(session.Windows[0].Bounds);
    }

    [Fact]
    public void LastWindowIdAndSheetSelectedFlagsAreIgnored()
    {
        var first = CreateFolder("first");
        var second = CreateFolder("second");
        WriteRaw($$"""
            {
              "last_window_id": 2,
              "windows": [
                {
                  "window_id": 1,
                  "folders": [{ "path": {{JsonSerializer.Serialize(first)}} }],
                  "buffers": [{ "sheets": [{ "selected": false }] }]
                },
                {
                  "window_id": 2,
                  "folders": [{ "path": {{JsonSerializer.Serialize(second)}} }],
                  "buffers": [{ "sheets": [{ "selected": true }] }]
                }
              ]
            }
            """);

        var session = Read();

        Assert.NotNull(session);
        Assert.Equal([1L, 2L], session.Windows.Select(window => window.WindowId));
        Assert.Equal(first, session.Windows[0].Folders[0].Path);
        Assert.Equal(second, session.Windows[1].Folders[0].Path);
    }

    [Fact]
    public void MalformedNonexistentAndDuplicateFoldersAreIgnoredDeterministically()
    {
        var valid = CreateFolder("valid");
        var nonexistent = Path.Combine(_temporaryDirectory, "absent");
        Write(new
        {
            windows = new[]
            {
                new
                {
                    window_id = 7,
                    folders = new[]
                    {
                        new { path = valid, name = (string?)null },
                        new { path = valid + Path.DirectorySeparatorChar, name = (string?)null },
                        new { path = nonexistent, name = (string?)null },
                        new { path = "relative", name = (string?)null },
                        new { path = "\0", name = (string?)null },
                    },
                },
            },
        });

        var session = Read();

        Assert.NotNull(session);
        Assert.Equal([valid], session.Windows[0].Folders.Select(folder => folder.Path));
    }

    [Fact]
    public void RelativeFolderUsesOnlyExplicitProjectDirectory()
    {
        var projectDirectory = CreateFolder("project");
        var child = Directory.CreateDirectory(Path.Combine(projectDirectory, "child")).FullName;
        Write(new
        {
            windows = new[]
            {
                new
                {
                    window_id = 7,
                    project = Path.Combine(projectDirectory, "sample.sublime-project"),
                    folders = new[] { new { path = "child" } },
                },
            },
        });

        Assert.Equal(child, Read()!.Windows[0].Folders[0].Path);
    }

    [Fact]
    public void RewrittenSessionIsObservedOnNextRead()
    {
        var first = CreateFolder("first");
        var second = CreateFolder("second");
        WriteSession(1, first);
        var reader = new SublimeTextSessionReader(SessionPath());

        Assert.Equal(first, reader.TryReadSession()!.Windows[0].Folders[0].Path);

        WriteSession(2, second);

        var updated = reader.TryReadSession();
        Assert.Equal(2, updated!.Windows[0].WindowId);
        Assert.Equal(second, updated.Windows[0].Folders[0].Path);
    }

    [Theory]
    [InlineData("0,0,1,-1,-1,-1,-1,1061,2506,228,4003,monitor", 2506, 228, 4003, 1061)]
    [InlineData("1061,2506,228,4003,monitor", 2506, 228, 4003, 1061)]
    public void ObservedPositionShapeParses(
        string position,
        int left,
        int top,
        int right,
        int bottom)
    {
        Assert.True(SublimeTextSessionReader.TryParsePosition(position, out var bounds));
        Assert.Equal(new SublimeTextWindowBounds(left, top, right, bottom), bounds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0,0,0,0,monitor")]
    [InlineData("bottom,left,top,right,monitor")]
    [InlineData("100,0,0,2000000,monitor")]
    public void MalformedOrImplausiblePositionIsRejected(string? position)
    {
        Assert.False(SublimeTextSessionReader.TryParsePosition(position, out var bounds));
        Assert.Null(bounds);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private SublimeTextSession? Read() => new SublimeTextSessionReader(SessionPath()).TryReadSession();

    private void WriteSession(long windowId, string folder)
        => Write(new
        {
            windows = new[]
            {
                new { window_id = windowId, folders = new[] { new { path = folder } } },
            },
        });

    private void Write(object value) => WriteRaw(JsonSerializer.Serialize(value));

    private void WriteRaw(string value) => File.WriteAllText(SessionPath(), value);

    private string CreateFolder(string name)
        => Directory.CreateDirectory(Path.Combine(_temporaryDirectory, name)).FullName;

    private string SessionPath() => Path.Combine(_temporaryDirectory, "Auto Save Session.sublime_session");
}
