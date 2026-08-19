using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class JetBrainsIdeLogReaderTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"GrepFlow-idea-log-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void ParsesVerifiedFrameAssignmentShapesAndMultipleProjects()
    {
        var googleKeep = CreateProject("GoogleKeepFlow");
        var steam = CreateProject("SteamFlow");
        var log = WriteLog(
            Frame("GoogleKeepFlow", "PRE_INIT", googleKeep) +
            Frame("SteamFlow", "COMPONENT_CREATED", steam));
        var reader = new JetBrainsIdeLogReader();

        Assert.Equal(googleKeep, reader.TryResolveProjectPath(log, "GoogleKeepFlow – Main.cs"));
        Assert.Equal(steam, reader.TryResolveProjectPath(log, "SteamFlow"));
    }

    [Fact]
    public void RepeatedRecordsAreDeduplicated()
    {
        var project = CreateProject("Repeated");
        var log = WriteLog(Frame("Repeated", "PRE_INIT", project) + Frame("Repeated", "COMPONENT_CREATED", project));

        Assert.Equal(project, new JetBrainsIdeLogReader().TryResolveProjectPath(log, "Repeated"));
    }

    [Fact]
    public void SameProjectNameWithDifferentExistingRootsIsAmbiguous()
    {
        var first = CreateProject(Path.Combine("first", "Shared"));
        var second = CreateProject(Path.Combine("second", "Shared"));
        var log = WriteLog(Frame("Shared", "PRE_INIT", first) + Frame("Shared", "PRE_INIT", second));

        Assert.Null(new JetBrainsIdeLogReader().TryResolveProjectPath(log, "Shared – file.kt"));
    }

    [Fact]
    public void SameProjectNameOutsideInitialTailIsStillAmbiguous()
    {
        var first = CreateProject(Path.Combine("first", "Shared"));
        var second = CreateProject(Path.Combine("second", "Shared"));
        var log = WriteLog(
            Frame("Shared", "PRE_INIT", first) +
            new string('x', 768) + "\n" +
            Frame("Shared", "COMPONENT_CREATED", second));
        var reader = new JetBrainsIdeLogReader(256);

        Assert.Null(reader.TryResolveProjectPath(log, "Shared – file.kt"));
        Assert.True(reader.LargestReadBytes <= 256);
        var bytesRead = reader.BytesRead;

        Assert.Null(reader.TryResolveProjectPath(log, "Shared – another.kt"));
        Assert.Equal(bytesRead, reader.BytesRead);
    }

    [Fact]
    public void FirstResolutionUsesBoundedReadsAndDiscardsPartialLeadingLine()
    {
        var current = CreateProject("Current");
        var log = WriteLog(new string('x', 512) + "\n" + Frame("Current", "PRE_INIT", current));
        var reader = new JetBrainsIdeLogReader(256);

        Assert.Equal(current, reader.TryResolveProjectPath(log, "Current"));
        Assert.True(reader.LargestReadBytes <= 256);
    }

    [Fact]
    public void ProjectOlderThanInitialTailIsFoundByBoundedBackwardSearch()
    {
        var old = CreateProject("Old");
        var log = WriteLog(Frame("Old", "PRE_INIT", old) + new string('x', 768) + "\n");
        var reader = new JetBrainsIdeLogReader(128);

        Assert.Equal(old, reader.TryResolveProjectPath(log, "Old – file.kt"));
        Assert.True(reader.BytesRead > 128);
        Assert.True(reader.LargestReadBytes <= 128);
        var bytesRead = reader.BytesRead;

        Assert.Equal(old, reader.TryResolveProjectPath(log, "Old – another.kt"));
        Assert.Equal(bytesRead, reader.BytesRead);
    }

    [Fact]
    public void LargeAppendPreservesPreviouslyKnownMappings()
    {
        var project = CreateProject("Known");
        var log = WriteLog(Frame("Known", "PRE_INIT", project));
        var reader = new JetBrainsIdeLogReader(256);
        Assert.Equal(project, reader.TryResolveProjectPath(log, "Known"));

        File.AppendAllText(log, string.Concat(Enumerable.Repeat(new string('x', 100) + "\n", 10)));

        Assert.Equal(project, reader.TryResolveProjectPath(log, "Known – file.kt"));
        Assert.True(reader.LargestReadBytes <= 256);
    }

    [Fact]
    public void AppendedRecordsAreObservedWithoutRereadingUnchangedPrefix()
    {
        var first = CreateProject("First");
        var second = CreateProject("Second");
        var firstRecord = Frame("First", "PRE_INIT", first);
        var log = WriteLog(firstRecord);
        var reader = new JetBrainsIdeLogReader();
        Assert.Equal(first, reader.TryResolveProjectPath(log, "First"));
        var initialBytes = reader.BytesRead;

        var secondRecord = Frame("Second", "PRE_INIT", second);
        File.AppendAllText(log, secondRecord);

        Assert.Equal(first, reader.TryResolveProjectPath(log, "First – file.kt"));
        Assert.Equal(initialBytes + System.Text.Encoding.UTF8.GetByteCount(secondRecord), reader.BytesRead);

        Assert.Equal(second, reader.TryResolveProjectPath(log, "Second"));
        Assert.Equal(reader.BytesRead, ReadAgain(reader, log, "Second"));
    }

    [Fact]
    public void TruncatedLogResetsCursorAndMappings()
    {
        var first = CreateProject("First");
        var second = CreateProject("B");
        var log = WriteLog(Frame("First", "PRE_INIT", first) + new string('x', 200) + "\n");
        var reader = new JetBrainsIdeLogReader();
        Assert.Equal(first, reader.TryResolveProjectPath(log, "First"));

        File.WriteAllText(log, Frame("B", "PRE_INIT", second));

        Assert.Null(reader.TryResolveProjectPath(log, "First"));
        Assert.Equal(second, reader.TryResolveProjectPath(log, "B"));
    }

    [Fact]
    public void ReplacedLogAtSameLengthResetsMappings()
    {
        var first = CreateProject("one");
        var second = CreateProject("two");
        var firstRecord = Frame("A", "PRE_INIT", first);
        var secondRecord = Frame("B", "PRE_INIT", second);
        Assert.Equal(firstRecord.Length, secondRecord.Length);
        var log = WriteLog(firstRecord);
        var reader = new JetBrainsIdeLogReader();
        Assert.Equal(first, reader.TryResolveProjectPath(log, "A"));
        var changedTime = File.GetLastWriteTimeUtc(log).AddSeconds(2);

        File.WriteAllText(log, secondRecord);
        File.SetLastWriteTimeUtc(log, changedTime);

        Assert.Null(reader.TryResolveProjectPath(log, "A"));
        Assert.Equal(second, reader.TryResolveProjectPath(log, "B"));
    }

    [Fact]
    public void PartiallyWrittenFinalLineIsCompletedLater()
    {
        var project = CreateProject("Partial");
        var record = Frame("Partial", "PRE_INIT", project).TrimEnd('\r', '\n');
        var log = WriteLog(record);
        var reader = new JetBrainsIdeLogReader();

        Assert.Null(reader.TryResolveProjectPath(log, "Partial"));
        File.AppendAllText(log, Environment.NewLine);

        Assert.Equal(project, reader.TryResolveProjectPath(log, "Partial"));
    }

    [Fact]
    public void MissingMalformedAndLockedLogsDoNotThrow()
    {
        var reader = new JetBrainsIdeLogReader();
        Assert.Null(reader.TryResolveProjectPath(Path.Combine(_temporaryDirectory, "missing.log"), "Project"));

        var malformed = WriteLog("garbage\nSetting project frame to Project(name=broken\n");
        Assert.Null(reader.TryResolveProjectPath(malformed, "broken"));

        using var locked = new FileStream(malformed, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(reader.TryResolveProjectPath(malformed, "broken"));
    }

    [Fact]
    public void NonexistentAndNonLocalComponentStorePathsAreRejected()
    {
        var nonexistent = Path.Combine(_temporaryDirectory, "missing");
        var log = WriteLog(
            Frame("Missing", "PRE_INIT", nonexistent) +
            Frame("Remote", "PRE_INIT", @"\\server\share\project") +
            Frame("Relative", "PRE_INIT", @"some\project"));
        var reader = new JetBrainsIdeLogReader();

        Assert.Null(reader.TryResolveProjectPath(log, "Missing"));
        Assert.Null(reader.TryResolveProjectPath(log, "Remote"));
        Assert.Null(reader.TryResolveProjectPath(log, "Relative"));
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private long ReadAgain(JetBrainsIdeLogReader reader, string log, string title)
    {
        var before = reader.BytesRead;
        Assert.NotNull(reader.TryResolveProjectPath(log, title));
        Assert.Equal(before, reader.BytesRead);
        return before;
    }

    private string CreateProject(string name) => Directory.CreateDirectory(Path.Combine(_temporaryDirectory, name)).FullName;

    private string WriteLog(string contents)
    {
        var path = Path.Combine(_temporaryDirectory, "idea.log");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Frame(string name, string state, string path)
        => $"2026-08-10 INFO - Setting project frame to Project(name={name}, containerState={state}, componentStore={path}){Environment.NewLine}";
}
