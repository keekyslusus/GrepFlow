using System.Runtime.InteropServices;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class VisualStudioDteWorkspaceReaderTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-{Guid.NewGuid():N}");

    public VisualStudioDteWorkspaceReaderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void OpenFolderDirectoryIsReturnedUnchanged()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "folder")).FullName;

        Assert.Equal(folder, ReadWorkspace(folder));
    }

    [Theory]
    [InlineData("solution.sln")]
    [InlineData("solution.slnx")]
    public void SolutionFileReturnsParentDirectory(string fileName)
    {
        var file = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllText(file, string.Empty);

        Assert.Equal(_temporaryDirectory, ReadWorkspace(file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.sln")]
    public void EmptyAndRelativePathsReturnNull(string? path)
    {
        Assert.Null(ReadWorkspace(path));
    }

    [Fact]
    public void NonexistentPathReturnsNull()
    {
        Assert.Null(ReadWorkspace(Path.Combine(_temporaryDirectory, "missing.sln")));
    }

    [Fact]
    public void DifferentDteWindowReturnsNull()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "folder")).FullName;
        var reader = CreateReader(new FakeDte(84, folder));

        Assert.Null(reader.TryReadWorkspace(new IntPtr(42)));
    }

    [Fact]
    public void SignedDteWindowHandleMatchesUnsignedNativeHandle()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "folder")).FullName;
        var handle = unchecked((int)0xF0000001);
        var reader = CreateReader(new FakeDte(handle, folder));

        Assert.Equal(folder, reader.TryReadWorkspace(new IntPtr(0xF0000001L)));
    }

    [Theory]
    [InlineData("!VisualStudio.DTE.17.0:13420", 13420U, true)]
    [InlineData("!VisualStudio.DTE.18.0:13420", 13420U, true)]
    [InlineData("!VisualStudio.DTE.19.3:13420", 13420U, true)]
    [InlineData("!VisualStudio.DTE.18.0:420", 13420U, false)]
    [InlineData("!VisualStudio.DTE.18.0:134200", 13420U, false)]
    [InlineData("!VisualStudio.DTE.18.0:13420:1", 13420U, false)]
    [InlineData("VisualStudio.DTE.18.0:13420", 13420U, false)]
    [InlineData("!VisualStudio.DTE.preview:13420", 13420U, false)]
    public void RotMonikerUsesVersionIndependentExactPidSuffix(
        string displayName,
        uint processId,
        bool expected)
    {
        Assert.Equal(expected, VisualStudioRunningObjectTable.MatchesDteMoniker(displayName, processId));
    }

    [Fact]
    public void MissingRotEntryReturnsNull()
    {
        var reader = new VisualStudioDteWorkspaceReader(_ => 13420, _ => null);

        Assert.Null(reader.TryReadWorkspace(new IntPtr(42)));
    }

    [Fact]
    public void RotComFailureReturnsNull()
    {
        var reader = new VisualStudioDteWorkspaceReader(
            _ => 13420,
            _ => throw new COMException("ROT entry disappeared"));

        Assert.Null(reader.TryReadWorkspace(new IntPtr(42)));
    }

    [Fact]
    public void DteComFailureReturnsNull()
    {
        var reader = CreateReader(new FailingDte());

        Assert.Null(reader.TryReadWorkspace(new IntPtr(42)));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private string? ReadWorkspace(string? path)
        => CreateReader(new FakeDte(42, path)).TryReadWorkspace(new IntPtr(42));

    private static VisualStudioDteWorkspaceReader CreateReader(object dte)
        => new(_ => 13420, processId => processId == 13420 ? dte : null);

    public sealed class FakeDte
    {
        public FakeDte(int window, string? fullName)
        {
            MainWindow = new FakeWindow(window);
            Solution = new FakeSolution(fullName);
        }

        public FakeWindow MainWindow { get; }

        public FakeSolution Solution { get; }
    }

    public sealed record FakeWindow(int HWnd);

    public sealed record FakeSolution(string? FullName);

    public sealed class FailingDte
    {
        public object MainWindow => throw new COMException("Visual Studio is busy");
    }
}
