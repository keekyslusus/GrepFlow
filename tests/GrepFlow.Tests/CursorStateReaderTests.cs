using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class CursorStateReaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _googleKeepFlow;
    private readonly string _playground;

    public CursorStateReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"));
        _googleKeepFlow = Path.Combine(_root, "GoogleKeepFlow");
        _playground = Path.Combine(_root, "playground");
        Directory.CreateDirectory(_googleKeepFlow);
        Directory.CreateDirectory(_playground);
    }

    [Fact]
    public void GlassUsesMatchingFirstWorkspaceDisplayPath()
    {
        var json = GlassJson(("GoogleKeepFlow", _googleKeepFlow), ("playground", _playground));

        Assert.Equal(_googleKeepFlow, CursorStateReader.ResolveGlass(" googlekeepflow ", json));
    }

    [Fact]
    public void NextReadObservesReorderedFirstGlassProject()
    {
        var value = GlassJson(("GoogleKeepFlow", _googleKeepFlow), ("playground", _playground));
        var reader = new CursorStateReader(_ => value);
        Assert.Equal(
            _googleKeepFlow,
            reader.TryReadActiveFolder(new CursorWindowSnapshot(CursorWindowMode.Glass, "GoogleKeepFlow")));

        value = GlassJson(("playground", _playground), ("GoogleKeepFlow", _googleKeepFlow));

        Assert.Equal(
            _playground,
            reader.TryReadActiveFolder(new CursorWindowSnapshot(CursorWindowMode.Glass, "playground")));
    }

    [Fact]
    public void GlassMismatchDoesNotSearchLaterSidebarProjects()
    {
        var json = GlassJson(("playground", _playground), ("GoogleKeepFlow", _googleKeepFlow));

        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("No Repo")]
    [InlineData("Select Workspace")]
    public void GlassRejectsUnavailableLabels(string label)
    {
        Assert.Null(CursorStateReader.ResolveGlass(label, GlassJson(("GoogleKeepFlow", _googleKeepFlow))));
    }

    [Fact]
    public void GlassRejectsNonWorkspaceAndRemoteProjects()
    {
        var repo = JsonSerializer.Serialize(new[]
        {
            new { type = "repo", name = "GoogleKeepFlow", displayPath = _googleKeepFlow },
        });
        var remote = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "workspace:opaque-id",
                type = "workspace",
                name = "GoogleKeepFlow",
                workspaceIdentifier = new { uri = new { external = "vscode-remote://ssh-remote/host/project" } },
            },
        });

        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", repo));
        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", remote));
    }

    [Fact]
    public void GlassRejectsRemoteAuthorityEvenWithExistingLocalDisplayPath()
    {
        var remote = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "workspace:opaque-id",
                type = "workspace",
                name = "GoogleKeepFlow",
                displayPath = _googleKeepFlow,
                workspaceIdentifier = new
                {
                    remoteAuthority = "ssh-remote+example",
                    uri = new { external = "vscode-remote://ssh-remote/example/project" },
                },
            },
        });

        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", remote));
    }

    [Fact]
    public void GlassRejectsRemoteUriEvenWithExistingLocalDisplayPath()
    {
        var remote = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "workspace:opaque-id",
                type = "workspace",
                name = "GoogleKeepFlow",
                displayPath = _googleKeepFlow,
                workspaceIdentifier = new
                {
                    uri = new { external = "vscode-remote://ssh-remote/example/project" },
                },
            },
        });

        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", remote));
    }

    [Fact]
    public void GlassAcceptsOpaqueWorkspaceIdForLocalWorkspace()
    {
        const string workspaceHash = "c957299c7cf9b493";
        var local = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = $"workspace:{workspaceHash}",
                type = "workspace",
                name = "GoogleKeepFlow",
                displayPath = _googleKeepFlow,
                workspaceIdentifier = new
                {
                    id = workspaceHash,
                    uri = new
                    {
                        scheme = "file",
                        external = FileUri(_googleKeepFlow),
                    },
                },
            },
        });

        Assert.Equal(_googleKeepFlow, CursorStateReader.ResolveGlass("GoogleKeepFlow", local));
    }

    [Fact]
    public void GlassRejectsRemoteUriInIdEvenWithExistingLocalDisplayPath()
    {
        var remote = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "vscode-remote://ssh-remote/example/project",
                type = "workspace",
                name = "GoogleKeepFlow",
                displayPath = _googleKeepFlow,
            },
        });

        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", remote));
    }

    [Fact]
    public void GlassRejectsWorkspaceSchemeOutsideIdProperty()
    {
        var external = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "workspace:opaque-id",
                type = "workspace",
                name = "GoogleKeepFlow",
                displayPath = _googleKeepFlow,
                workspaceIdentifier = new { external = "workspace:opaque-id" },
            },
        });
        var uri = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "workspace:opaque-id",
                type = "workspace",
                name = "GoogleKeepFlow",
                displayPath = _googleKeepFlow,
                workspaceIdentifier = new { uri = "workspace:opaque-id" },
            },
        });

        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", external));
        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", uri));
    }

    [Fact]
    public void GlassUsesLocalWorkspaceIdentifierWhenDisplayPathIsMissing()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                type = "workspace",
                name = "GoogleKeepFlow",
                workspaceIdentifier = new { uri = new { external = FileUri(_googleKeepFlow) } },
            },
        });

        Assert.Equal(_googleKeepFlow, CursorStateReader.ResolveGlass("GoogleKeepFlow", json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void MalformedOrIncompleteGlassJsonReturnsNull(string json)
    {
        Assert.Null(CursorStateReader.ResolveGlass("GoogleKeepFlow", json));
    }

    [Fact]
    public void IdeSelectsUniqueMatchingFolderEvenWhenNotFirst()
    {
        var history = HistoryJson(_playground, _googleKeepFlow);

        Assert.Equal(
            _googleKeepFlow,
            CursorStateReader.ResolveIde("GoogleKeepFlow", history, () => null));
    }

    [Fact]
    public void IdeEmptyLabelDoesNotReturnMostRecentFolder()
    {
        Assert.Null(CursorStateReader.ResolveIde("", HistoryJson(_googleKeepFlow), () => null));
    }

    [Fact]
    public void IdeRejectsAmbiguousBasenames()
    {
        var secondParent = Path.Combine(_root, "other");
        var duplicate = Path.Combine(secondParent, "GoogleKeepFlow");
        Directory.CreateDirectory(duplicate);
        var history = HistoryJson(_googleKeepFlow, duplicate);

        Assert.Null(CursorStateReader.ResolveIde("GoogleKeepFlow", history, () => null));
    }

    [Fact]
    public void IdeUsesMetadataFallbackWhenHistoryHasNoMatch()
    {
        var metadata = JsonSerializer.Serialize(new
        {
            entries = new[] { new { displayPath = _googleKeepFlow } },
        });

        Assert.Equal(
            _googleKeepFlow,
            CursorStateReader.ResolveIde("GoogleKeepFlow", HistoryJson(_playground), () => metadata));
    }

    [Fact]
    public void IdeMetadataRejectsRemoteAuthorityWithExistingLocalDisplayPath()
    {
        var metadata = JsonSerializer.Serialize(new
        {
            entries = new[]
            {
                new
                {
                    displayPath = _googleKeepFlow,
                    workspaceIdentifier = new { remoteAuthority = "ssh-remote+example" },
                },
            },
        });

        Assert.Null(CursorStateReader.ResolveIde(
            "GoogleKeepFlow",
            HistoryJson(_playground),
            () => metadata));
    }

    [Fact]
    public void IdeMetadataRejectsRemoteUriWithExistingLocalDisplayPath()
    {
        var metadata = JsonSerializer.Serialize(new
        {
            entries = new[]
            {
                new
                {
                    displayPath = _googleKeepFlow,
                    workspaceIdentifier = new
                    {
                        uri = new { external = "vscode-remote://ssh-remote/example/project" },
                    },
                },
            },
        });

        Assert.Null(CursorStateReader.ResolveIde(
            "GoogleKeepFlow",
            HistoryJson(_playground),
            () => metadata));
    }

    [Fact]
    public void IdeDoesNotUseMetadataToOverrideAmbiguousHistory()
    {
        var duplicate = Path.Combine(_root, "duplicate", "GoogleKeepFlow");
        Directory.CreateDirectory(duplicate);
        var metadata = JsonSerializer.Serialize(new { entries = new[] { new { displayPath = _googleKeepFlow } } });

        Assert.Null(CursorStateReader.ResolveIde(
            "GoogleKeepFlow",
            HistoryJson(_googleKeepFlow, duplicate),
            () => metadata));
    }

    [Fact]
    public void NormalizeAcceptsEncodedFileUriAndTrimsTrailingSeparator()
    {
        var spaced = Path.Combine(_root, "folder with spaces");
        Directory.CreateDirectory(spaced);

        Assert.Equal(spaced, CursorStateReader.NormalizeLocalPath(FileUri(spaced) + "/"));
    }

    [Fact]
    public void NormalizeUppercasesPercentEncodedDriveLetter()
    {
        var suffix = _googleKeepFlow[2..].Replace('\\', '/').Replace(" ", "%20");
        var uri = $"file:///c%3A{suffix}";

        Assert.Equal(_googleKeepFlow, CursorStateReader.NormalizeLocalPath(uri));
    }

    [Fact]
    public void NormalizeDoesNotDecodeFileUriPathTwice()
    {
        var literalPercent = Path.Combine(_root, "literal%20name");
        Directory.CreateDirectory(literalPercent);

        Assert.Equal(literalPercent, CursorStateReader.NormalizeLocalPath(FileUri(literalPercent)));
    }

    [Fact]
    public void NormalizePreservesDriveRoot()
    {
        var driveRoot = Path.GetPathRoot(_root);

        Assert.Equal(driveRoot, CursorStateReader.NormalizeLocalPath(driveRoot));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("https://example.com/project")]
    [InlineData("vscode-remote://ssh-remote/host/project")]
    [InlineData("file://server/share/project")]
    [InlineData("file:///c%3A/bad%ZZpath")]
    public void NormalizeRejectsNonLocalPaths(string value)
    {
        Assert.Null(CursorStateReader.NormalizeLocalPath(value));
    }

    [Fact]
    public void NormalizeRejectsMissingAndUncDirectories()
    {
        Assert.Null(CursorStateReader.NormalizeLocalPath(Path.Combine(_root, "missing")));
        Assert.Null(CursorStateReader.NormalizeLocalPath(@"\\server\share\project"));
    }

    [Fact]
    public void NativeSqliteReaderReadsUtf8AndFreshCommittedValues()
    {
        var databasePath = Path.Combine(_root, "state.vscdb");
        using var database = new NativeSqliteFixture(databasePath);
        database.Put(CursorStateReader.GlassProjectsKey, "первое значение");
        var reader = new CursorSqliteValueReader(databasePath);

        Assert.Equal("первое значение", reader.TryReadString(CursorStateReader.GlassProjectsKey));

        database.Put(CursorStateReader.GlassProjectsKey, "следующее значение");
        Assert.Equal("следующее значение", reader.TryReadString(CursorStateReader.GlassProjectsKey));
    }

    [Fact]
    public void NativeSqliteReaderReturnsNullForMissingAndCorruptDatabase()
    {
        var missing = Path.Combine(_root, "missing.vscdb");
        Assert.Null(new CursorSqliteValueReader(missing).TryReadString("key"));

        var corrupt = Path.Combine(_root, "corrupt.vscdb");
        File.WriteAllText(corrupt, "not a sqlite database");
        Assert.Null(new CursorSqliteValueReader(corrupt).TryReadString("key"));
    }

    [Fact]
    public void NativeSqliteBusyReadReturnsWithinBoundedTime()
    {
        var databasePath = Path.Combine(_root, "busy.vscdb");
        using var database = new NativeSqliteFixture(databasePath);
        database.Put("key", "value");
        database.BeginExclusive();
        var stopwatch = Stopwatch.StartNew();

        var value = new CursorSqliteValueReader(databasePath).TryReadString("key");

        stopwatch.Stop();
        Assert.Null(value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Read took {stopwatch.Elapsed}");
    }

    private static string GlassJson(params (string Name, string Path)[] projects)
        => JsonSerializer.Serialize(projects.Select(project => new
        {
            type = "workspace",
            name = project.Name,
            displayPath = project.Path,
        }));

    private static string HistoryJson(params string[] paths)
        => JsonSerializer.Serialize(new
        {
            entries = paths.Select(path => new { folderUri = FileUri(path) }).ToArray(),
        });

    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class NativeSqliteFixture : IDisposable
    {
        private static readonly IntPtr SqliteTransient = new(-1);
        private IntPtr _database;

        public NativeSqliteFixture(string path)
        {
            Assert.Equal(0, sqlite3_open_v2(Utf8(path), out _database, 0x00000006, IntPtr.Zero));
            Exec("PRAGMA journal_mode=WAL");
            Exec("CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT)");
        }

        public void Put(string key, string value)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                Assert.Equal(0, sqlite3_prepare_v2(
                    _database,
                    Utf8("INSERT OR REPLACE INTO ItemTable(key, value) VALUES(?1, ?2)"),
                    -1,
                    out statement,
                    IntPtr.Zero));
                var keyBytes = Utf8(key);
                var valueBytes = Utf8(value);
                Assert.Equal(0, sqlite3_bind_text(statement, 1, keyBytes, keyBytes.Length - 1, SqliteTransient));
                Assert.Equal(0, sqlite3_bind_text(statement, 2, valueBytes, valueBytes.Length - 1, SqliteTransient));
                Assert.Equal(101, sqlite3_step(statement));
            }
            finally
            {
                if (statement != IntPtr.Zero) _ = sqlite3_finalize(statement);
            }
        }

        public void BeginExclusive()
        {
            Exec("PRAGMA journal_mode=DELETE");
            Exec("BEGIN EXCLUSIVE");
        }

        private void Exec(string sql)
        {
            var result = sqlite3_exec(_database, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var error);
            try
            {
                Assert.True(result == 0, Marshal.PtrToStringUTF8(error) ?? $"SQLite error {result}");
            }
            finally
            {
                if (error != IntPtr.Zero) sqlite3_free(error);
            }
        }

        public void Dispose()
        {
            if (_database == IntPtr.Zero) return;
            _ = sqlite3_exec(_database, Utf8("ROLLBACK"), IntPtr.Zero, IntPtr.Zero, out _);
            _ = sqlite3_close_v2(_database);
            _database = IntPtr.Zero;
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_exec(
            IntPtr database,
            byte[] sql,
            IntPtr callback,
            IntPtr callbackArgument,
            out IntPtr errorMessage);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void sqlite3_free(IntPtr value);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_prepare_v2(
            IntPtr database,
            byte[] sql,
            int byteCount,
            out IntPtr statement,
            IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_bind_text(
            IntPtr statement,
            int index,
            byte[] value,
            int byteCount,
            IntPtr destructor);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_close_v2(IntPtr database);
    }
}
