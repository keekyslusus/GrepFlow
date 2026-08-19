using System.Text.Json;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class CodexCliSessionReaderTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-Codex-{Guid.NewGuid():N}");

    public CodexCliSessionReaderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void MissingCodexHomeReturnsNoSessions()
    {
        var reader = new CodexCliSessionReader(Path.Combine(_temporaryDirectory, "missing"));

        Assert.Empty(reader.ReadActiveSessions());
    }

    [Fact]
    public void MissingLocksDirectoryReturnsNoSessions()
    {
        Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "sessions"));

        Assert.Empty(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());
    }

    [Fact]
    public void CoordinationLockIsIgnored()
    {
        CreateLayout();
        File.WriteAllText(Path.Combine(LocksDirectory(), ".coordination.lock"), "");

        Assert.Empty(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());
    }

    [Fact]
    public void CliSessionMetadataReturnsExistingWorkingDirectory()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "project")).FullName;
        WriteSession("session-one", folder);

        var session = Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());

        Assert.Equal("session-one", session.SessionId);
        Assert.Equal(folder, session.WorkingDirectory);
        Assert.Equal(folder, session.InitialWorkingDirectory);
    }

    [Fact]
    public void NonCliSessionIsIgnored()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "project")).FullName;
        WriteSession("vscode-session", folder, source: "vscode");

        Assert.Empty(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());
    }

    [Fact]
    public void MalformedMetadataIsIgnored()
    {
        WriteSessionFile("broken-session", "{");

        Assert.Empty(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());
    }

    [Theory]
    [InlineData("relative-folder")]
    [InlineData("missing-folder")]
    public void InvalidWorkingDirectoryIsIgnored(string folderName)
    {
        var folder = folderName == "relative-folder"
            ? folderName
            : Path.Combine(_temporaryDirectory, folderName);
        WriteSession("invalid-session", folder);

        Assert.Empty(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());
    }

    [Fact]
    public void LockTimestampRepresentsResumedSessionActivity()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "project")).FullName;
        var paths = WriteSession("resumed-session", folder);
        var oldRolloutTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var resumedTime = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(paths.Rollout, oldRolloutTime);
        File.SetLastWriteTimeUtc(paths.Lock, resumedTime);

        var session = Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());

        Assert.Equal(resumedTime, session.LastActivityUtc);
    }

    [Fact]
    public void CachedReaderDiscoversNewActiveSession()
    {
        var first = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "second")).FullName;
        WriteSession("first-session", first);
        var reader = new CodexCliSessionReader(_temporaryDirectory);
        Assert.Single(reader.ReadActiveSessions());

        WriteSession("second-session", second);

        Assert.Equal(2, reader.ReadActiveSessions().Count);
    }

    [Fact]
    public void ReadActiveSessionReturnsOnlyRequestedLiveSession()
    {
        var first = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "second")).FullName;
        WriteSession("first-session", first);
        WriteSession("second-session", second);
        var reader = new CodexCliSessionReader(_temporaryDirectory);

        var session = reader.ReadActiveSession("second-session");

        Assert.NotNull(session);
        Assert.Equal("second-session", session.SessionId);
        Assert.Equal(second, session.WorkingDirectory);
        Assert.Null(reader.ReadActiveSession("inactive-session"));
    }

    [Fact]
    public void TargetedActiveSessionReadObservesAppendedTurnContext()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "target-initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "target-resumed")).FullName;
        var paths = WriteSession("target-session", initial);
        var reader = new CodexCliSessionReader(_temporaryDirectory);
        Assert.Equal(initial, reader.ReadActiveSession("target-session")?.WorkingDirectory);

        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = resumed } });

        var session = reader.ReadActiveSession("target-session");
        Assert.Equal(resumed, session?.WorkingDirectory);
        Assert.Equal(initial, session?.InitialWorkingDirectory);
    }

    [Fact]
    public void RolloutCanBeReadWhileOpenForWriting()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "project")).FullName;
        var paths = WriteSession("open-session", folder);
        using var writer = new FileStream(
            paths.Rollout,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var session = Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());

        Assert.Equal(folder, session.WorkingDirectory);
    }

    [Fact]
    public void OnlyFirstRolloutLineIsParsed()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "project")).FullName;
        var paths = WriteSession("first-line-session", folder);
        File.AppendAllText(paths.Rollout, Environment.NewLine + "{ malformed history");

        Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());
    }

    [Fact]
    public void LatestValidTurnContextOverridesInitialWorkingDirectory()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "resumed")).FullName;
        var paths = WriteSession("resumed-session", initial);
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = resumed } });

        var session = Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions());

        Assert.Equal(resumed, session.WorkingDirectory);
        Assert.Equal(initial, session.InitialWorkingDirectory);
    }

    [Fact]
    public void LastValidTurnContextWins()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var first = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "first-context")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "second-context")).FullName;
        var paths = WriteSession("several-contexts", initial);
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = first } });
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = second } });

        Assert.Equal(
            second,
            Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void MalformedFinalLineDoesNotHideEarlierValidTurnContext()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "resumed")).FullName;
        var paths = WriteSession("malformed-tail", initial);
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = resumed } });
        File.AppendAllText(paths.Rollout, Environment.NewLine + "{broken");

        Assert.Equal(
            resumed,
            Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void JsonWithTrailingGarbageDoesNotHideEarlierValidTurnContext()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var valid = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "valid")).FullName;
        var malformed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "malformed")).FullName;
        var paths = WriteSession("trailing-garbage", initial);
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = valid } });
        var json = JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd = malformed } });
        File.AppendAllText(paths.Rollout, Environment.NewLine + json + "garbage");

        Assert.Equal(
            valid,
            Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void TurnContextAllowsTrailingJsonWhitespace()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "resumed")).FullName;
        var paths = WriteSession("trailing-whitespace", initial);
        var json = JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd = resumed } });
        File.AppendAllText(paths.Rollout, Environment.NewLine + json + " \t");

        Assert.Equal(
            resumed,
            Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void InvalidTurnContextsAreIgnoredWhileSearchingBackward()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var valid = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "valid")).FullName;
        var paths = WriteSession("invalid-contexts", initial);
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = valid } });
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = "relative" } });
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = @"\\server\share" } });
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = Path.Combine(_temporaryDirectory, "missing") } });
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = 42 } });
        File.AppendAllText(paths.Rollout, Environment.NewLine + "{bad json");

        Assert.Equal(
            valid,
            Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void ContextOutsideTailCapFallsBackToInitialDirectory()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var oldContext = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "old-context")).FullName;
        var paths = WriteSession("capped-tail", initial);
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = oldContext } });
        File.AppendAllText(paths.Rollout, Environment.NewLine + new string('x', CodexCliSessionReader.MaxTailBytes + 1024));

        Assert.Equal(
            initial,
            Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void OversizedPartialLineIsDiscardedWithoutHidingEarlierContext()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "resumed")).FullName;
        var paths = WriteSession("oversized-line", initial);
        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = resumed } });
        File.AppendAllText(
            paths.Rollout,
            Environment.NewLine + new string('x', 3 * 1024 * 1024));
        var reader = new CodexCliSessionReader(_temporaryDirectory);
        Assert.Equal(resumed, Assert.Single(reader.ReadActiveSessions()).WorkingDirectory);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var session = Assert.Single(reader.ReadActiveSessions());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(resumed, session.WorkingDirectory);
        Assert.True(
            allocated < 16 * 1024 * 1024,
            $"Tail read allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void TurnContextCrossingReadBufferBoundaryIsParsed()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "resumed")).FullName;
        var paths = WriteSession("split-context", initial);
        AppendItem(paths.Rollout, new
        {
            type = "turn_context",
            payload = new { cwd = resumed, padding = new string('x', 70 * 1024) },
        });

        Assert.Equal(
            resumed,
            Assert.Single(new CodexCliSessionReader(_temporaryDirectory).ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void CachedRolloutPathObservesAppendedTurnContext()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "resumed")).FullName;
        var paths = WriteSession("cached-rollout", initial);
        var reader = new CodexCliSessionReader(_temporaryDirectory);
        Assert.Equal(initial, Assert.Single(reader.ReadActiveSessions()).WorkingDirectory);

        AppendItem(paths.Rollout, new { type = "turn_context", payload = new { cwd = resumed } });

        Assert.Equal(resumed, Assert.Single(reader.ReadActiveSessions()).WorkingDirectory);
    }

    [Fact]
    public void OpenWriterCanAppendTurnContextBetweenReads()
    {
        var initial = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "initial")).FullName;
        var resumed = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "resumed")).FullName;
        var paths = WriteSession("growing-rollout", initial);
        var reader = new CodexCliSessionReader(_temporaryDirectory);
        using var writer = new FileStream(
            paths.Rollout,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(initial, Assert.Single(reader.ReadActiveSessions()).WorkingDirectory);

        using (var text = new StreamWriter(writer, leaveOpen: true))
        {
            text.WriteLine();
            text.Write(JsonSerializer.Serialize(new { type = "turn_context", payload = new { cwd = resumed } }));
            text.Flush();
        }

        Assert.Equal(resumed, Assert.Single(reader.ReadActiveSessions()).WorkingDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private (string Lock, string Rollout) WriteSession(string sessionId, string cwd, string source = "cli")
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new { source, cwd },
        });
        return WriteSessionFile(sessionId, json);
    }

    private (string Lock, string Rollout) WriteSessionFile(string sessionId, string firstLine)
    {
        CreateLayout();
        var lockPath = Path.Combine(LocksDirectory(), $"{sessionId}.lock");
        var rolloutPath = Path.Combine(SessionsDirectory(), $"rollout-2026-08-10-{sessionId}.jsonl");
        File.WriteAllText(lockPath, "");
        File.WriteAllText(rolloutPath, firstLine);
        return (lockPath, rolloutPath);
    }

    private void CreateLayout()
    {
        Directory.CreateDirectory(LocksDirectory());
        Directory.CreateDirectory(SessionsDirectory());
    }

    private string LocksDirectory() => Path.Combine(_temporaryDirectory, "thread-writer-locks");

    private string SessionsDirectory() => Path.Combine(_temporaryDirectory, "sessions", "2026", "08", "10");

    private static void AppendItem(string rolloutPath, object item)
        => File.AppendAllText(rolloutPath, Environment.NewLine + JsonSerializer.Serialize(item));
}
