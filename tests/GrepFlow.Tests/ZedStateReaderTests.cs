using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class ZedStateReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"GrepFlow-Zed-{Guid.NewGuid():N}");

    public ZedStateReaderTests()
    {
        System.IO.Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DefaultStatePathPointsToStableDatabaseUnderLocalAppData()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zed", "db", "0-stable", "db.sqlite");

        Assert.Equal(expected, ZedStateReader.DefaultStatePath());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("project \u2014 Program.cs")]
    [InlineData("project \u2199")]
    [InlineData("project \u2197")]
    [InlineData("project \u2014 Program.cs \u2199")]
    [InlineData("project \u2014 Program.cs \u2197")]
    public void SupportedTitlesResolveSingleLocalRoot(string title)
    {
        var project = CreateDirectory("project");
        var reader = Reader([project]);

        Assert.Equal(project, reader.TryReadActiveFolder(title));
    }

    [Fact]
    public void SuppliedTitleIsReevaluatedOnEveryRead()
    {
        var first = CreateDirectory("first");
        var second = CreateDirectory("second");
        var reads = 0;
        var reader = new ZedStateReader(
            () =>
            {
                reads++;
                return [new(first), new(second)];
            });

        Assert.Equal(first, reader.TryReadActiveFolder("first"));
        Assert.Equal(second, reader.TryReadActiveFolder("second"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void NonmatchingRowOrderDoesNotOverrideTitleMatch()
    {
        var wanted = CreateDirectory("wanted");
        var newer = CreateDirectory("newer");

        Assert.Equal(wanted, Reader([newer, wanted]).TryReadActiveFolder("wanted"));
    }

    [Fact]
    public void NonmatchingHistoricalPathsAreNotProbedForExistence()
    {
        var probed = new List<string>();
        var reader = new ZedStateReader(
            () => [new(@"Z:\repo"), new(@"C:\current")],
            path =>
            {
                probed.Add(path);
                return true;
            });

        Assert.Equal(@"C:\current", reader.TryReadActiveFolder("current"));
        Assert.Equal([@"C:\current"], probed);
    }

    [Fact]
    public void AmbiguousMatchingPathsAreRejectedBeforeExistenceProbes()
    {
        var probeCalls = 0;
        var reader = new ZedStateReader(
            () => [new(@"C:\one\repo"), new(@"D:\two\repo")],
            _ =>
            {
                probeCalls++;
                return true;
            });

        Assert.Null(reader.TryReadActiveFolder("repo"));
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public void DistinctSameNamedRootsAreAmbiguous()
    {
        var first = CreateDirectory(Path.Combine("one", "repo"));
        var second = CreateDirectory(Path.Combine("two", "repo"));

        Assert.Null(Reader([first, second]).TryReadActiveFolder("repo"));
    }

    [Fact]
    public void DuplicateNormalizedRowsDoNotCreateAmbiguity()
    {
        var project = CreateDirectory("repo");

        Assert.Equal(
            project,
            Reader([project, project + Path.DirectorySeparatorChar, project]).TryReadActiveFolder("repo"));
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("foobar", "foobar")]
    [InlineData("foo \u2014 file.txt", "foo")]
    public void RootNamePrefixCollisionsAreDistinguished(string title, string expectedName)
    {
        var foo = CreateDirectory("foo");
        var foobar = CreateDirectory("foobar");

        Assert.Equal(
            expectedName == "foo" ? foo : foobar,
            Reader([foo, foobar]).TryReadActiveFolder(title));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("projectile")]
    [InlineData("project \u2014 ")]
    [InlineData("project random")]
    [InlineData("project \u2199 extra")]
    public void UnsupportedOrLooseTitlesReturnNull(string title)
    {
        var project = CreateDirectory("project");

        Assert.Null(Reader([project]).TryReadActiveFolder(title));
    }

    [Fact]
    public void EmptyMissingRelativeUncAndFilePathsReturnNull()
    {
        var missing = Path.Combine(_root, "missing");
        var file = Path.Combine(_root, "file.txt");
        File.WriteAllText(file, "content");
        var values = new[] { "", "relative", @"\\server\share\repo", missing, file };

        foreach (var value in values)
            Assert.Null(Reader([value]).TryReadActiveFolder("repo"));

        Assert.Null(Reader([]).TryReadActiveFolder("empty project"));
    }

    [Fact]
    public void MultiRootSerializedPathsAreRejected()
    {
        var one = CreateDirectory("one");
        var two = CreateDirectory("two");

        Assert.Null(Reader([$"{one}\n{two}"]).TryReadActiveFolder("one, two"));
    }

    [Fact]
    public void NativeReaderExcludesRemoteRowsAndReadsWalWhileWriterIsOpen()
    {
        var databasePath = Path.Combine(_root, "db.sqlite");
        var local = CreateDirectory(Path.Combine("local", "repo"));
        var remote = CreateDirectory(Path.Combine("remote", "repo"));
        using var database = new NativeZedFixture(databasePath);
        database.Put(local);
        database.Put(remote, remoteConnectionId: 5);
        var reader = new ZedStateReader(new ZedSqliteWorkspaceReader(databasePath).TryReadEntries);

        Assert.Equal(local, reader.TryReadActiveFolder("repo"));
    }

    [Fact]
    public void NativeReaderObservesInsertedStateOnNextRead()
    {
        var databasePath = Path.Combine(_root, "db.sqlite");
        var first = CreateDirectory("first");
        var second = CreateDirectory("second");
        using var database = new NativeZedFixture(databasePath);
        database.Put(first);
        var reader = new ZedStateReader(new ZedSqliteWorkspaceReader(databasePath).TryReadEntries);
        Assert.Equal(first, reader.TryReadActiveFolder("first"));

        database.Put(second);

        Assert.Equal(second, reader.TryReadActiveFolder("second"));
    }

    [Fact]
    public void MissingCorruptAndChangedSchemaDatabasesReturnNull()
    {
        var missing = Path.Combine(_root, "missing.sqlite");
        Assert.Null(new ZedSqliteWorkspaceReader(missing).TryReadEntries());

        var corrupt = Path.Combine(_root, "corrupt.sqlite");
        File.WriteAllText(corrupt, "not sqlite");
        Assert.Null(new ZedSqliteWorkspaceReader(corrupt).TryReadEntries());

        var changed = Path.Combine(_root, "changed.sqlite");
        using var database = new NativeZedFixture(changed, createSchema: false);
        database.Exec("CREATE TABLE other (value TEXT)");
        Assert.Null(new ZedSqliteWorkspaceReader(changed).TryReadEntries());
    }

    [Fact]
    public void ExclusiveLockReturnsNullWithinBoundedTime()
    {
        var databasePath = Path.Combine(_root, "busy.sqlite");
        using var database = new NativeZedFixture(databasePath);
        database.Put(CreateDirectory("repo"));
        database.BeginExclusive();
        var stopwatch = Stopwatch.StartNew();

        var entries = new ZedSqliteWorkspaceReader(databasePath).TryReadEntries();

        stopwatch.Stop();
        Assert.Null(entries);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Read took {stopwatch.Elapsed}");
    }

    [Fact]
    public void MalformedUtf8TextReturnsNull()
    {
        var databasePath = Path.Combine(_root, "utf8.sqlite");
        using var database = new NativeZedFixture(databasePath);
        database.Exec("INSERT INTO workspaces(paths, remote_connection_id) VALUES(CAST(X'80' AS TEXT), NULL)");

        Assert.Null(new ZedSqliteWorkspaceReader(databasePath).TryReadEntries());
    }

    public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);

    private string CreateDirectory(string relative)
    {
        var path = Path.Combine(_root, relative);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private static ZedStateReader Reader(params string[] paths)
        => new(() => paths.Select(path => new ZedWorkspaceEntry(path)).ToArray());

    private sealed class NativeZedFixture : IDisposable
    {
        private IntPtr _database;

        public NativeZedFixture(string path, bool createSchema = true)
        {
            Assert.Equal(0, sqlite3_open_v2(Utf8(path), out _database, 0x00000006, IntPtr.Zero));
            Exec("PRAGMA journal_mode=WAL");
            if (createSchema)
                Exec("CREATE TABLE workspaces (paths TEXT, remote_connection_id INTEGER)");
        }

        public void Put(string paths, int? remoteConnectionId = null)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                Assert.Equal(0, sqlite3_prepare_v2(
                    _database,
                    Utf8("INSERT INTO workspaces(paths, remote_connection_id) VALUES(?1, ?2)"),
                    -1,
                    out statement,
                    IntPtr.Zero));
                var pathBytes = Utf8(paths);
                Assert.Equal(0, sqlite3_bind_text(statement, 1, pathBytes, pathBytes.Length - 1, new IntPtr(-1)));
                if (remoteConnectionId is null)
                    Assert.Equal(0, sqlite3_bind_null(statement, 2));
                else
                    Assert.Equal(0, sqlite3_bind_int(statement, 2, remoteConnectionId.Value));
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

        public void Exec(string sql)
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
        private static extern int sqlite3_bind_null(IntPtr statement, int index);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_bind_int(IntPtr statement, int index, int value);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_close_v2(IntPtr database);
    }
}
