using System.Text.Json;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class ClaudeCodeSessionReaderTests : IDisposable
{
    private const ulong StartTime = 123456789;
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-Claude-{Guid.NewGuid():N}");
    private readonly Dictionary<uint, WindowsProcessIdentity?> _identities = [];

    public ClaudeCodeSessionReaderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void MissingConfigDirectoryReturnsEmptyList()
    {
        Assert.Empty(Reader(Path.Combine(_temporaryDirectory, "missing")).ReadLiveSessions());
    }

    [Fact]
    public void MissingSessionsDirectoryReturnsEmptyList()
    {
        Assert.Empty(Reader(_temporaryDirectory).ReadLiveSessions());
    }

    [Fact]
    public void ConfiguredAbsoluteDirectoryWinsWithoutExistingFallback()
    {
        var configured = Path.Combine(_temporaryDirectory, "configured");
        var fallback = Path.Combine(_temporaryDirectory, "profile");

        Assert.Equal(
            Path.GetFullPath(configured),
            ClaudeCodeSessionReader.ResolveConfigDirectory(configured, fallback));
    }

    [Fact]
    public void RelativeConfiguredDirectoryFallsBackToUserProfile()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "profile")).FullName;

        Assert.Equal(
            Path.Combine(profile, ".claude"),
            ClaudeCodeSessionReader.ResolveConfigDirectory("relative", profile));
    }

    [Fact]
    public void ValidInteractiveCliPointerIsReturned()
    {
        var folder = CreateFolder("project");
        AddClaudeIdentity(42);
        WritePointer(42, folder);

        var session = Assert.Single(Reader().ReadLiveSessions());

        Assert.Equal(42u, session.ProcessId);
        Assert.Equal("session-42", session.SessionId);
        Assert.Equal(folder, session.WorkingDirectory);
        Assert.Equal(StartTime, session.ProcessStartFileTime);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("busy")]
    [InlineData(null)]
    public void StatusIsInformational(string? status)
    {
        var folder = CreateFolder("project");
        AddClaudeIdentity(42);
        WritePointer(42, folder, status: status);

        Assert.Single(Reader().ReadLiveSessions());
    }

    [Theory]
    [InlineData("background", "cli")]
    [InlineData("interactive", "desktop")]
    public void NonInteractiveOrNonCliEntriesAreIgnored(string kind, string entrypoint)
    {
        var folder = CreateFolder("project");
        AddClaudeIdentity(42);
        WritePointer(42, folder, kind: kind, entrypoint: entrypoint);

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void MalformedJsonIsIgnored()
    {
        CreateSessionsDirectory();
        File.WriteAllText(Path.Combine(SessionsDirectory(), "42.json"), "{");

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void MissingWorkingDirectoryIsIgnored()
    {
        CreateSessionsDirectory();
        AddClaudeIdentity(42);
        File.WriteAllText(
            Path.Combine(SessionsDirectory(), "42.json"),
            JsonSerializer.Serialize(new
            {
                pid = 42,
                sessionId = "session-42",
                procStart = StartTime,
                kind = "interactive",
                entrypoint = "cli",
            }));

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void MissingSessionIdIsIgnored()
    {
        CreateSessionsDirectory();
        var folder = CreateFolder("project");
        AddClaudeIdentity(42);
        File.WriteAllText(
            Path.Combine(SessionsDirectory(), "42.json"),
            JsonSerializer.Serialize(new
            {
                pid = 42,
                cwd = folder,
                procStart = StartTime,
                kind = "interactive",
                entrypoint = "cli",
            }));

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Theory]
    [InlineData(0, "cwd")]
    [InlineData(-1, "cwd")]
    [InlineData(42, "relative")]
    [InlineData(42, "missing")]
    [InlineData(42, "unc")]
    [InlineData(42, "blank")]
    public void InvalidPidOrWorkingDirectoryIsIgnored(int pid, string cwdKind)
    {
        var cwd = cwdKind switch
        {
            "cwd" => CreateFolder("project"),
            "relative" => "relative-folder",
            "missing" => Path.Combine(_temporaryDirectory, "missing"),
            "unc" => "\\\\server\\share",
            _ => "",
        };
        if (pid > 0) AddClaudeIdentity((uint)pid);
        WritePointerRaw(pid, cwd);

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void DeadPidIsIgnored()
    {
        WritePointer(42, CreateFolder("project"));

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void LiveNonClaudePidIsIgnored()
    {
        _identities[42] = new WindowsProcessIdentity(42, "node.exe", StartTime);
        WritePointer(42, CreateFolder("project"));

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void MismatchedProcessStartIsIgnored()
    {
        _identities[42] = new WindowsProcessIdentity(42, "claude.exe", StartTime + 1);
        WritePointer(42, CreateFolder("project"));

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void PointerWithoutProcessStartRejectsLiveClaude()
    {
        AddClaudeIdentity(42);
        WritePointer(42, CreateFolder("project"), includeProcessStart: false);

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void LegacyPointerWithoutProcessStartRejectsDeadPid()
    {
        WritePointer(42, CreateFolder("project"), includeProcessStart: false);

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void PointerWithoutProcessStartRejectsReusedClaudePid()
    {
        _identities[42] = new WindowsProcessIdentity(42, "claude.exe", StartTime + 1);
        WritePointer(42, CreateFolder("stale-project"), includeProcessStart: false);

        Assert.Empty(Reader().ReadLiveSessions());
    }

    [Fact]
    public void MissingConfiguredDirectoryDoesNotFallBackToAnotherConfigRoot()
    {
        var fallback = Path.Combine(_temporaryDirectory, "fallback");
        var folder = CreateFolder("project");
        AddClaudeIdentity(42);
        var fallbackSessions = Directory.CreateDirectory(Path.Combine(fallback, "sessions")).FullName;
        File.WriteAllText(Path.Combine(fallbackSessions, "42.json"), PointerJson(42, folder));
        var configured = Path.Combine(_temporaryDirectory, "configured-but-not-created");

        Assert.Empty(Reader(configured).ReadLiveSessions());
    }

    [Fact]
    public void PointerCanBeReadWhileOpenForWriting()
    {
        AddClaudeIdentity(42);
        var path = WritePointer(42, CreateFolder("project"));
        using var writer = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.Single(Reader().ReadLiveSessions());
    }

    [Fact]
    public void RemovingPointerIsReflectedByNextRead()
    {
        AddClaudeIdentity(42);
        var path = WritePointer(42, CreateFolder("project"));
        var reader = Reader();
        Assert.Single(reader.ReadLiveSessions());

        File.Delete(path);

        Assert.Empty(reader.ReadLiveSessions());
    }

    [Fact]
    public void NewPointerIsReflectedByNextRead()
    {
        var reader = Reader();
        Assert.Empty(reader.ReadLiveSessions());
        AddClaudeIdentity(42);

        WritePointer(42, CreateFolder("project"));

        Assert.Single(reader.ReadLiveSessions());
    }

    [Fact]
    public void BadFileDoesNotHideValidSession()
    {
        CreateSessionsDirectory();
        File.WriteAllText(Path.Combine(SessionsDirectory(), "bad.json"), "{");
        AddClaudeIdentity(42);
        WritePointer(42, CreateFolder("project"));

        Assert.Single(Reader().ReadLiveSessions());
    }

    [Fact]
    public void NestedPointerIsNotEnumerated()
    {
        var folder = CreateFolder("project");
        AddClaudeIdentity(42);
        var nested = Directory.CreateDirectory(Path.Combine(SessionsDirectory(), "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "42.json"), PointerJson(42, folder));

        Assert.Empty(Reader().ReadLiveSessions());
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private ClaudeCodeSessionReader Reader(string? configDirectory = null)
        => new(configDirectory ?? _temporaryDirectory, pid => _identities.GetValueOrDefault(pid));

    private void AddClaudeIdentity(uint pid)
        => _identities[pid] = new WindowsProcessIdentity(pid, "claude.exe", StartTime);

    private string WritePointer(
        uint pid,
        string cwd,
        string kind = "interactive",
        string entrypoint = "cli",
        string? status = "idle",
        bool includeProcessStart = true)
    {
        CreateSessionsDirectory();
        var path = Path.Combine(SessionsDirectory(), $"{pid}.json");
        File.WriteAllText(path, PointerJson(pid, cwd, kind, entrypoint, status, includeProcessStart));
        return path;
    }

    private void WritePointerRaw(int pid, string cwd)
    {
        CreateSessionsDirectory();
        File.WriteAllText(
            Path.Combine(SessionsDirectory(), "invalid.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["pid"] = pid,
                ["sessionId"] = "session-invalid",
                ["cwd"] = cwd,
                ["procStart"] = StartTime,
                ["kind"] = "interactive",
                ["entrypoint"] = "cli",
            }));
    }

    private static string PointerJson(
        uint pid,
        string cwd,
        string kind = "interactive",
        string entrypoint = "cli",
        string? status = "idle",
        bool includeProcessStart = true)
    {
        var values = new Dictionary<string, object?>
        {
            ["pid"] = pid,
            ["sessionId"] = $"session-{pid}",
            ["cwd"] = cwd,
            ["kind"] = kind,
            ["entrypoint"] = entrypoint,
        };
        if (status is not null) values["status"] = status;
        if (includeProcessStart) values["procStart"] = StartTime;
        return JsonSerializer.Serialize(values);
    }

    private void CreateSessionsDirectory() => Directory.CreateDirectory(SessionsDirectory());

    private string SessionsDirectory() => Path.Combine(_temporaryDirectory, "sessions");

    private string CreateFolder(string name)
        => Directory.CreateDirectory(Path.Combine(_temporaryDirectory, name)).FullName;
}
