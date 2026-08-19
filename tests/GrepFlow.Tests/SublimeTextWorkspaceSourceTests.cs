using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class SublimeTextWorkspaceSourceTests
{
    private static readonly IntPtr FirstHwnd = new(42);
    private static readonly IntPtr SecondHwnd = new(84);

    [Fact]
    public async Task NeverActivatedSourceDoesNotInspectOrReadSession()
    {
        var inspectCalls = 0;
        var readCalls = 0;
        var source = new SublimeTextWorkspaceSource(
            _ =>
            {
                inspectCalls++;
                return Snapshot(FirstHwnd, 1);
            },
            () =>
            {
                readCalls++;
                return Session(Window(1, @"C:\repo"));
            },
            _ => true);

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, inspectCalls);
        Assert.Equal(0, readCalls);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Sublime Text\sublime_text.exe")]
    [InlineData(@"C:\Program Files\Sublime Text\SUBLIME_TEXT.EXE")]
    public void SublimeImageNameMatchesCaseInsensitively(string imagePath)
    {
        Assert.True(SublimeTextWindowInspector.MatchesImageName(imagePath));
    }

    [Theory]
    [InlineData(@"C:\Tools\subl.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(null)]
    public void UnrelatedImageDoesNotMatch(string? imagePath)
    {
        Assert.False(SublimeTextWindowInspector.MatchesImageName(imagePath));
    }

    [Fact]
    public void MatchesForegroundDoesNotReadSession()
    {
        var readCalls = 0;
        var source = Source(
            _ => Snapshot(FirstHwnd, 1),
            () =>
            {
                readCalls++;
                return Session(Window(1, @"C:\repo"));
            });

        Assert.True(source.MatchesForeground(FirstHwnd));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task OwnedDialogOrUnrelatedWindowDoesNotReplaceLastProjectFrame()
    {
        var source = Source(
            window => window == FirstHwnd ? Snapshot(FirstHwnd, 1) : null,
            () => Session(Window(1, @"C:\repo")));
        Assert.True(source.MatchesForeground(FirstHwnd));

        Assert.False(source.MatchesForeground(SecondHwnd));

        Assert.Equal(@"C:\repo", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task ClosedOrReusedWindowClearsStateWithoutReadingSession()
    {
        var processId = 1u;
        var readCalls = 0;
        var source = Source(
            _ => Snapshot(FirstHwnd, processId),
            () =>
            {
                readCalls++;
                return Session(Window(1, @"C:\repo"));
            });
        Assert.True(source.MatchesForeground(FirstHwnd));
        processId = 2;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readCalls);
        Assert.Equal(0, source.CachedWindowCount);
    }

    [Fact]
    public async Task AlreadyCancelledLookupThrowsBeforeReaderWork()
    {
        var readCalls = 0;
        var source = Source(
            _ => Snapshot(FirstHwnd, 1),
            () =>
            {
                readCalls++;
                return Session(Window(1, @"C:\repo"));
            });
        source.MatchesForeground(FirstHwnd);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.GetActiveFolderAsync(cancellation.Token));
        Assert.Equal(0, readCalls);
    }

    [Fact]
    public async Task TwoNativeWindowsKeepIndependentAssociationsAndSelectedRoots()
    {
        var titles = new Dictionary<IntPtr, string>
        {
            [FirstHwnd] = @"C:\one\a.txt (one, two) - Sublime Text",
            [SecondHwnd] = @"C:\three\c.txt (three, four) - Sublime Text",
        };
        var source = Source(
            window => Snapshot(window, window == FirstHwnd ? 1u : 2u, titles[window]),
            () => Session(
                Window(11, @"C:\one", @"C:\two"),
                Window(22, @"C:\three", @"C:\four")));

        Assert.True(source.MatchesForeground(FirstHwnd));
        Assert.Equal(@"C:\one", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
        titles[FirstHwnd] = @"C:\two\b.txt (one, two) - Sublime Text";
        Assert.Equal(@"C:\two", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        Assert.True(source.MatchesForeground(SecondHwnd));
        Assert.Equal(@"C:\three", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        titles[FirstHwnd] = @"untitled - Sublime Text";
        Assert.True(source.MatchesForeground(FirstHwnd));
        Assert.Equal(@"C:\two", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public void NativeWindowCacheIsBounded()
    {
        var source = Source(
            window => Snapshot(window, checked((uint)window.ToInt64())),
            () => Session(Window(1, @"C:\repo")));

        for (var value = 1; value <= SublimeTextWorkspaceSource.MaxCachedWindows + 10; value++)
            Assert.True(source.MatchesForeground(new IntPtr(value)));

        Assert.Equal(SublimeTextWorkspaceSource.MaxCachedWindows, source.CachedWindowCount);
    }

    [Fact]
    public void OneRootAlwaysWinsRegardlessOfTitleOrRememberedRoot()
    {
        Assert.Equal(
            @"C:\repo",
            SublimeTextWorkspaceSource.SelectRoot(
                @"C:\outside\file.txt - Sublime Text",
                Folders(@"C:\repo"),
                @"C:\old"));
    }

    [Fact]
    public void ActiveFilesSelectEitherRootAndNestedRootsPreferLongestMatch()
    {
        var folders = Folders(@"C:\repo", @"C:\repo\nested");

        Assert.Equal(
            @"C:\repo",
            SublimeTextWorkspaceSource.SelectRoot(@"C:\repo\one.txt - Sublime Text", folders, null));
        Assert.Equal(
            @"C:\repo\nested",
            SublimeTextWorkspaceSource.SelectRoot(
                @"C:\repo\nested\two.txt - Sublime Text",
                folders,
                @"C:\repo"));
    }

    [Fact]
    public void OutsideUntitledAndSimilarPrefixTitlesKeepRememberedRoot()
    {
        var folders = Folders(@"C:\repo", @"C:\repo-two");

        Assert.Equal(
            @"C:\repo-two",
            SublimeTextWorkspaceSource.SelectRoot(@"C:\outside\file.txt - Sublime Text", folders, @"C:\repo-two"));
        Assert.Equal(
            @"C:\repo-two",
            SublimeTextWorkspaceSource.SelectRoot("untitled - Sublime Text", folders, @"C:\repo-two"));
        Assert.Equal(
            @"C:\repo",
            SublimeTextWorkspaceSource.SelectRoot(@"C:\repo-twoish\file.txt", folders, @"C:\repo"));
    }

    [Fact]
    public void FirstRootIsFallbackAndMissingRememberedRootResetsToFirst()
    {
        var folders = Folders(@"C:\first", @"C:\second");

        Assert.Equal(@"C:\first", SublimeTextWorkspaceSource.SelectRoot("external.txt", folders, null));
        Assert.Equal(@"C:\first", SublimeTextWorkspaceSource.SelectRoot("external.txt", folders, @"C:\removed"));
    }

    [Fact]
    public void FolderReorderingPreservesRememberedRootAndNoFoldersReturnsNull()
    {
        Assert.Equal(
            @"C:\second",
            SublimeTextWorkspaceSource.SelectRoot(
                "untitled",
                Folders(@"C:\second", @"C:\first"),
                @"C:\second"));
        Assert.Null(SublimeTextWorkspaceSource.SelectRoot("title", [], @"C:\second"));
    }

    [Fact]
    public void CachedWindowIdSurvivesSessionArrayReordering()
    {
        var native = Snapshot(FirstHwnd, 1, "unparseable");
        var first = Window(11, @"C:\first");
        var second = Window(22, @"C:\second");

        Assert.Same(second, SublimeTextWorkspaceSource.MatchSessionWindow(native, [second, first], 22));
    }

    [Fact]
    public void OnlySessionWindowMapsWithoutTitleParsing()
    {
        var only = Window(11, @"C:\first");

        Assert.Same(
            only,
            SublimeTextWorkspaceSource.MatchSessionWindow(Snapshot(FirstHwnd, 1, ""), [only], null));
    }

    [Fact]
    public void FreshGeometryMapsWindowAndStaleGeometryFallsThroughToLabels()
    {
        var first = Window(11, new SublimeTextWindowBounds(0, 0, 1000, 800), @"C:\GrepFlow");
        var second = Window(22, new SublimeTextWindowBounds(1100, 0, 2100, 800), @"C:\other");
        var fresh = Snapshot(FirstHwnd, 1, "untitled", new SublimeTextWindowBounds(10, 8, 1008, 808));
        var stale = Snapshot(FirstHwnd, 1, "file - other - Sublime Text", new SublimeTextWindowBounds(4000, 0, 5000, 800));

        Assert.Same(first, SublimeTextWorkspaceSource.MatchSessionWindow(fresh, [first, second], null));
        Assert.Same(second, SublimeTextWorkspaceSource.MatchSessionWindow(stale, [first, second], null));
    }

    [Fact]
    public void OrderedFolderLabelsCanDistinguishWindows()
    {
        var multi = Window(11, @"C:\GrepFlow", @"C:\highlight_plugins");
        var other = Window(22, @"C:\vibeclown_scripts");

        Assert.Same(
            multi,
            SublimeTextWorkspaceSource.MatchSessionWindow(
                Snapshot(FirstHwnd, 1, "GrepFlow, highlight_plugins - Sublime Text"),
                [multi, other],
                null));
        Assert.Same(
            other,
            SublimeTextWorkspaceSource.MatchSessionWindow(
                Snapshot(SecondHwnd, 1, "tool.py (vibeclown_scripts) - Sublime Text"),
                [multi, other],
                null));
    }

    [Fact]
    public void ExternalActiveFilePathCannotAssociateNativeWindow()
    {
        var alpha = Window(11, @"C:\alpha");
        var beta = Window(22, @"C:\beta");

        Assert.Null(
            SublimeTextWorkspaceSource.MatchSessionWindow(
                Snapshot(SecondHwnd, 1, @"C:\alpha\readme.md - Sublime Text"),
                [alpha, beta],
                null));
    }

    [Fact]
    public void ExternalActiveFileUsesSeparateWorkspaceLabelForAssociation()
    {
        var alpha = Window(11, @"C:\alpha");
        var beta = Window(22, @"C:\beta");

        Assert.Same(
            beta,
            SublimeTextWorkspaceSource.MatchSessionWindow(
                Snapshot(SecondHwnd, 1, @"C:\alpha\readme.md (beta) - Sublime Text"),
                [alpha, beta],
                null));
    }

    [Fact]
    public void CurrentUnregisteredTitleExtractsWorkspaceLabelBeforeSublimeSuffix()
    {
        const string title = "plugin.log (GrepFlow, highlight_plugins) - Sublime Text (UNREGISTERED)";

        Assert.Equal(
            "GrepFlow, highlight_plugins",
            SublimeTextWorkspaceSource.ExtractWorkspaceLabel(title));

        var expected = Window(11, @"C:\GrepFlow", @"C:\highlight_plugins");
        var other = Window(22, @"C:\vibeclown_scripts");
        var staleGeometry = new SublimeTextWindowBounds(0, 0, 1000, 800);
        expected = expected with { Bounds = new SublimeTextWindowBounds(1100, 0, 2100, 800) };
        other = other with { Bounds = staleGeometry };

        Assert.Same(
            expected,
            SublimeTextWorkspaceSource.MatchSessionWindow(
                Snapshot(FirstHwnd, 1, title, staleGeometry),
                [expected, other],
                null));
    }

    [Theory]
    [InlineData("file.txt (beta) \u2014 Sublime Text")]
    [InlineData("file.txt (beta) \u2013 Sublime Text")]
    [InlineData("file.txt (beta) \u2014 Sublime Text (UNREGISTERED)")]
    [InlineData("file.txt (beta) \u2013 Sublime Text (UNREGISTERED)")]
    public void UnicodeDashSublimeSuffixesAreRecognized(string title)
    {
        Assert.Equal("beta", SublimeTextWorkspaceSource.ExtractWorkspaceLabel(title));
    }

    [Fact]
    public void OptionalFolderNameParticipatesInAssociation()
    {
        var named = new SublimeTextSessionWindow(
            11,
            null,
            null,
            null,
            [new SublimeTextSessionFolder(@"C:\opaque", "Friendly workspace")]);
        var other = Window(22, @"C:\other");

        Assert.Same(
            named,
            SublimeTextWorkspaceSource.MatchSessionWindow(
                Snapshot(FirstHwnd, 1, "file.txt - Friendly workspace - Sublime Text"),
                [named, other],
                null));
    }

    [Fact]
    public void AmbiguousCandidatesDoNotCreateAssociation()
    {
        var first = Window(11, @"C:\one");
        var second = Window(22, @"D:\one");
        var native = Snapshot(FirstHwnd, 1, "one - Sublime Text");

        Assert.Null(SublimeTextWorkspaceSource.MatchSessionWindow(native, [first, second], null));
    }

    [Fact]
    public void ExactWorkspaceLabelOverridesContradictoryGeometry()
    {
        var alpha = Window(11, new SublimeTextWindowBounds(0, 0, 1000, 800), @"C:\alpha");
        var beta = Window(22, new SublimeTextWindowBounds(1100, 0, 2100, 800), @"C:\beta");
        var native = Snapshot(
            SecondHwnd,
            1,
            "readme.md (beta) - Sublime Text",
            new SublimeTextWindowBounds(0, 0, 1000, 800));

        Assert.Same(beta, SublimeTextWorkspaceSource.MatchSessionWindow(native, [alpha, beta], null));
    }

    [Fact]
    public async Task QueryDoesNotOverwriteNewerForegroundWindow()
    {
        SublimeTextWorkspaceSource? source = null;
        var alphaInspections = 0;
        var switched = false;
        source = Source(
            window =>
            {
                if (window == FirstHwnd)
                {
                    alphaInspections++;
                    if (alphaInspections == 2 && !switched)
                    {
                        switched = true;
                        source!.MatchesForeground(SecondHwnd);
                    }

                    return Snapshot(FirstHwnd, 1, "file.txt (alpha) - Sublime Text");
                }

                return Snapshot(SecondHwnd, 1, "file.txt (beta) - Sublime Text");
            },
            () => Session(Window(11, @"C:\alpha"), Window(22, @"C:\beta")));
        source.MatchesForeground(FirstHwnd);

        Assert.Equal(@"C:\alpha", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
        Assert.Equal(@"C:\beta", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task SameProcessHwndReuseRejectsContradictedCachedAssociation()
    {
        var title = "file.txt (alpha) - Sublime Text";
        var source = Source(
            _ => Snapshot(FirstHwnd, 7, title),
            () => Session(Window(11, @"C:\alpha"), Window(22, @"C:\beta")));
        source.MatchesForeground(FirstHwnd);
        Assert.Equal(@"C:\alpha", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        title = "file.txt (beta) - Sublime Text";
        Assert.True(source.MatchesForeground(FirstHwnd));

        Assert.Equal(@"C:\beta", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    [Fact]
    public async Task TransientReadFailureReusesRootOnlyForRevalidatedIdentity()
    {
        SublimeTextSession? session = Session(Window(11, @"C:\one"));
        var processId = 1u;
        var source = Source(
            _ => Snapshot(FirstHwnd, processId),
            () => session);
        source.MatchesForeground(FirstHwnd);
        Assert.Equal(@"C:\one", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        session = null;
        Assert.Equal(@"C:\one", (await source.GetActiveFolderAsync(CancellationToken.None))?.Path);

        processId = 2;
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
    }

    private static SublimeTextWorkspaceSource Source(
        Func<IntPtr, SublimeTextWindowSnapshot?> inspector,
        Func<SublimeTextSession?> reader)
        => new(inspector, reader, _ => true);

    private static SublimeTextWindowSnapshot Snapshot(
        IntPtr hwnd,
        uint processId,
        string title = "untitled - Sublime Text",
        SublimeTextWindowBounds? bounds = null)
        => new(hwnd, processId, @"C:\Sublime Text\sublime_text.exe", title, IntPtr.Zero, bounds);

    private static SublimeTextSession Session(params SublimeTextSessionWindow[] windows) => new(windows);

    private static SublimeTextSessionWindow Window(long id, params string[] roots)
        => Window(id, null, roots);

    private static SublimeTextSessionWindow Window(
        long id,
        SublimeTextWindowBounds? bounds,
        params string[] roots)
        => new(id, bounds, null, null, Folders(roots));

    private static IReadOnlyList<SublimeTextSessionFolder> Folders(params string[] roots)
        => roots.Select(root => new SublimeTextSessionFolder(root, null)).ToArray();
}
