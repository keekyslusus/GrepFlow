using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class WindowsProcessTreeTests
{
    [Fact]
    public void SnapshotFindsImagesAndDescendantRelationships()
    {
        var snapshot = new WindowsProcessSnapshot(
        [
            new WindowsProcessInfo(10, 1, "WindowsTerminal.exe"),
            new WindowsProcessInfo(20, 10, "cmd.exe"),
            new WindowsProcessInfo(30, 20, "codex.exe"),
        ]);

        Assert.True(snapshot.ContainsImage("CODEX.EXE"));
        Assert.True(snapshot.IsDescendant(30, 10));
        Assert.True(snapshot.HasDescendantImage(10, "codex.exe"));
        Assert.False(snapshot.IsDescendant(10, 10));
        Assert.False(snapshot.IsDescendant(10, 30));
    }

    [Fact]
    public void DescendantEnumerationReturnsAssociatedProcessIds()
    {
        var snapshot = new WindowsProcessSnapshot(
        [
            new WindowsProcessInfo(10, 1, "WindowsTerminal.exe"),
            new WindowsProcessInfo(20, 10, "cmd.exe"),
            new WindowsProcessInfo(30, 20, "codex.exe"),
            new WindowsProcessInfo(40, 10, "host.exe"),
            new WindowsProcessInfo(50, 40, "CODEX.EXE"),
        ]);

        Assert.Equal([30u, 50u], snapshot.FindDescendantProcesses(10, "codex.exe").Order());
    }

    [Fact]
    public void DuplicateProcessEntriesDoNotProduceDuplicateIds()
    {
        var snapshot = new WindowsProcessSnapshot(
        [
            new WindowsProcessInfo(10, 1, "WindowsTerminal.exe"),
            new WindowsProcessInfo(30, 10, "codex.exe"),
            new WindowsProcessInfo(30, 10, "codex.exe"),
        ]);

        Assert.Equal([30u], snapshot.FindDescendantProcesses(10, "codex.exe"));
    }

    [Fact]
    public void MissingAncestorReturnsNoDescendants()
    {
        var snapshot = new WindowsProcessSnapshot(
        [
            new WindowsProcessInfo(30, 99, "codex.exe"),
        ]);

        Assert.Empty(snapshot.FindDescendantProcesses(99, "codex.exe"));
    }

    [Fact]
    public void CyclicParentDataTerminatesSafely()
    {
        var snapshot = new WindowsProcessSnapshot(
        [
            new WindowsProcessInfo(10, 20, "one.exe"),
            new WindowsProcessInfo(20, 10, "two.exe"),
        ]);

        Assert.False(snapshot.IsDescendant(10, 99));
        Assert.Empty(snapshot.FindDescendantProcesses(10, "codex.exe"));
    }

    [Fact]
    public void LiveSnapshotContainsCurrentProcess()
    {
        var currentImage = Path.GetFileName(Environment.ProcessPath);

        var snapshot = new WindowsProcessTree().Capture();

        Assert.NotNull(currentImage);
        Assert.True(snapshot.ContainsImage(currentImage));
    }
}
