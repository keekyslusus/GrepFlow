using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class JetBrainsIdeWorkspaceSourceTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"GrepFlow-jetbrains-source-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task NeverActivatedAndroidStudioSourceDoesNotInvokeReader()
    {
        var readerCalls = 0;
        var source = CreateSource(AndroidStudioProfile(), _ => null, _ =>
        {
            readerCalls++;
            return _temporaryDirectory;
        });

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readerCalls);
    }

    [Fact]
    public async Task NeverActivatedPyCharmSourceDoesNotInvokeReader()
    {
        var readerCalls = 0;
        var source = CreateSource(PyCharmProfile(), _ => null, _ =>
        {
            readerCalls++;
            return _temporaryDirectory;
        });

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readerCalls);
    }

    [Theory]
    [InlineData("idea.exe")]
    [InlineData("IDEA.EXE")]
    [InlineData("idea64.exe")]
    [InlineData("IDEA64.EXE")]
    public void IntelliJExecutablesMatchCaseInsensitively(string imageName)
    {
        Assert.True(JetBrainsIdeWorkspaceSource.MatchesImageName(IntelliJIdeaProfile(), imageName));
    }

    [Theory]
    [InlineData("studio64.exe")]
    [InlineData("STUDIO64.EXE")]
    public void AndroidStudioExecutablesMatchCaseInsensitively(string imageName)
    {
        Assert.True(JetBrainsIdeWorkspaceSource.MatchesImageName(AndroidStudioProfile(), imageName));
    }

    [Theory]
    [InlineData("pycharm.exe")]
    [InlineData("PYCHARM.EXE")]
    [InlineData("pycharm64.exe")]
    [InlineData("PYCHARM64.EXE")]
    public void PyCharmExecutablesMatchCaseInsensitively(string imageName)
    {
        Assert.True(JetBrainsIdeWorkspaceSource.MatchesImageName(PyCharmProfile(), imageName));
    }

    [Theory]
    [InlineData("studio.exe")]
    [InlineData("STUDIO.EXE")]
    [InlineData("idea.cmd")]
    [InlineData("studio-helper.exe")]
    [InlineData("Code.exe")]
    public void UnrelatedExecutablesDoNotMatch(string imageName)
    {
        Assert.False(JetBrainsIdeWorkspaceSource.MatchesImageName(IntelliJIdeaProfile(), imageName));
        Assert.False(JetBrainsIdeWorkspaceSource.MatchesImageName(AndroidStudioProfile(), imageName));
        Assert.False(JetBrainsIdeWorkspaceSource.MatchesImageName(PyCharmProfile(), imageName));
    }

    [Fact]
    public void ProductExecutableSetsDoNotOverlap()
    {
        var idea = IntelliJIdeaProfile().ExecutableFileNames;
        var studio = AndroidStudioProfile().ExecutableFileNames;
        var pyCharm = PyCharmProfile().ExecutableFileNames;

        Assert.Empty(idea.Intersect(studio, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(idea.Intersect(pyCharm, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(studio.Intersect(pyCharm, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SunAwtDialog")]
    [InlineData("#32770")]
    [InlineData("")]
    public void DialogClassIsNotAProjectFrame(string className)
    {
        Assert.False(JetBrainsIdeWorkspaceSource.IsProjectFrame(className, IntPtr.Zero));
        Assert.True(JetBrainsIdeWorkspaceSource.IsProjectFrame("SunAwtFrame", IntPtr.Zero));
    }

    [Fact]
    public void OwnedFrameIsNotAProjectFrame()
    {
        Assert.False(JetBrainsIdeWorkspaceSource.IsProjectFrame("SunAwtFrame", new IntPtr(7)));
    }

    [Fact]
    public void MatchesForegroundDoesNotPerformReaderWork()
    {
        var readerCalls = 0;
        var source = CreateSource(AndroidStudioProfile(), AlwaysStudio, _ =>
        {
            readerCalls++;
            return _temporaryDirectory;
        });

        Assert.True(source.MatchesForeground(new IntPtr(42)));
        Assert.Equal(0, readerCalls);
    }

    [Fact]
    public async Task ActivatedAndroidStudioSourceReturnsResolvedFolderAndMetadata()
    {
        var source = CreateSource(AndroidStudioProfile(), AlwaysStudio, _ => _temporaryDirectory);
        Assert.True(source.MatchesForeground(new IntPtr(42)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(_temporaryDirectory, active?.Path);
        Assert.Equal("Android Studio", active?.SourceName);
        Assert.False(active?.FromNearestWindow);
        Assert.Equal("android-studio", source.Id);
    }

    [Fact]
    public async Task ActivatedIntelliJSourcePreservesResolvedFolderAndMetadata()
    {
        var source = CreateSource(IntelliJIdeaProfile(), window => Idea(window), _ => _temporaryDirectory);
        Assert.True(source.MatchesForeground(new IntPtr(42)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(_temporaryDirectory, active?.Path);
        Assert.Equal("IntelliJ IDEA", active?.SourceName);
        Assert.False(active?.FromNearestWindow);
        Assert.Equal("intellij-idea", source.Id);
    }

    [Fact]
    public async Task ActivatedPyCharmSourceReturnsResolvedFolderAndMetadata()
    {
        var source = CreateSource(PyCharmProfile(), window => PyCharm(window), _ => _temporaryDirectory);
        Assert.True(source.MatchesForeground(new IntPtr(42)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);

        Assert.Equal(_temporaryDirectory, active?.Path);
        Assert.Equal("PyCharm", active?.SourceName);
        Assert.False(active?.FromNearestWindow);
        Assert.Equal("pycharm", source.Id);
    }

    [Fact]
    public async Task ClosedOrReusedCachedWindowDoesNotInvokeReader()
    {
        var windowIsStudio = true;
        var readerCalls = 0;
        var source = CreateSource(
            AndroidStudioProfile(),
            window => windowIsStudio ? Studio(window) : null,
            _ =>
            {
                readerCalls++;
                return _temporaryDirectory;
            });
        Assert.True(source.MatchesForeground(new IntPtr(42)));
        windowIsStudio = false;

        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Null(await source.GetActiveFolderAsync(CancellationToken.None));
        Assert.Equal(0, readerCalls);
    }

    [Fact]
    public async Task UnrelatedForegroundWindowDoesNotDiscardLastStudioProjectWindow()
    {
        var studioWindow = new IntPtr(42);
        var source = CreateSource(
            AndroidStudioProfile(),
            window => window == studioWindow ? Studio(window) : null,
            _ => _temporaryDirectory);
        Assert.True(source.MatchesForeground(studioWindow));

        Assert.False(source.MatchesForeground(new IntPtr(84)));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);
        Assert.Equal(_temporaryDirectory, active?.Path);
    }

    [Fact]
    public async Task StudioDialogDoesNotDisplaceLastProjectWindow()
    {
        var projectWindow = new IntPtr(42);
        var dialogWindow = new IntPtr(84);
        var source = CreateSource(
            AndroidStudioProfile(),
            window => window == projectWindow ? Studio(window) : null,
            process => process.Window == projectWindow ? _temporaryDirectory : null);
        Assert.True(source.MatchesForeground(projectWindow));

        Assert.False(source.MatchesForeground(dialogWindow));

        var active = await source.GetActiveFolderAsync(CancellationToken.None);
        Assert.Equal(_temporaryDirectory, active?.Path);
    }

    [Fact]
    public async Task CancellationIsObservedBeforeReaderWork()
    {
        var readerCalls = 0;
        var source = CreateSource(AndroidStudioProfile(), AlwaysStudio, _ =>
        {
            readerCalls++;
            return _temporaryDirectory;
        });
        source.MatchesForeground(new IntPtr(42));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await source.GetActiveFolderAsync(cancellation.Token));
        Assert.Equal(0, readerCalls);
    }

    [Fact]
    public async Task ProductSourcesKeepIndependentCachedWindows()
    {
        var ideaWindow = new IntPtr(42);
        var studioWindow = new IntPtr(84);
        var pyCharmWindow = new IntPtr(126);
        var ideaProject = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "idea")).FullName;
        var studioProject = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "studio")).FullName;
        var pyCharmProject = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "pycharm")).FullName;
        var idea = CreateSource(
            IntelliJIdeaProfile(),
            window => window == ideaWindow ? Idea(window) : null,
            _ => ideaProject);
        var studio = CreateSource(
            AndroidStudioProfile(),
            window => window == studioWindow ? Studio(window) : null,
            _ => studioProject);
        var pyCharm = CreateSource(
            PyCharmProfile(),
            window => window == pyCharmWindow ? PyCharm(window) : null,
            _ => pyCharmProject);

        Assert.True(idea.MatchesForeground(ideaWindow));
        Assert.True(studio.MatchesForeground(studioWindow));
        Assert.True(pyCharm.MatchesForeground(pyCharmWindow));

        Assert.Equal(ideaProject, (await idea.GetActiveFolderAsync(CancellationToken.None))?.Path);
        Assert.Equal(studioProject, (await studio.GetActiveFolderAsync(CancellationToken.None))?.Path);
        Assert.Equal(pyCharmProject, (await pyCharm.GetActiveFolderAsync(CancellationToken.None))?.Path);
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private static JetBrainsIdeWorkspaceSource CreateSource(
        JetBrainsIdeProfile profile,
        Func<IntPtr, JetBrainsIdeProcessWindow?> inspect,
        Func<JetBrainsIdeProcessWindow, string?> read)
        => new(profile, inspect, read);

    private static JetBrainsIdeProfile IntelliJIdeaProfile()
        => new("intellij-idea", "IntelliJ IDEA", ["idea.exe", "idea64.exe"], "JetBrains", "IntelliJIdea");

    private static JetBrainsIdeProfile AndroidStudioProfile()
        => new("android-studio", "Android Studio", ["studio64.exe"], "Google", "AndroidStudio");

    private static JetBrainsIdeProfile PyCharmProfile()
        => new("pycharm", "PyCharm", ["pycharm.exe", "pycharm64.exe"], "JetBrains", "PyCharm");

    private static JetBrainsIdeProcessWindow? AlwaysStudio(IntPtr window) => Studio(window);

    private static JetBrainsIdeProcessWindow Idea(IntPtr window)
        => new(window, 123, @"C:\Program Files\JetBrains\IntelliJ IDEA\bin\idea64.exe");

    private static JetBrainsIdeProcessWindow Studio(IntPtr window)
        => new(window, 456, @"C:\Program Files\Android\Android Studio\bin\studio64.exe");

    private static JetBrainsIdeProcessWindow PyCharm(IntPtr window)
        => new(window, 789, @"C:\Program Files\JetBrains\PyCharm\bin\pycharm64.exe");
}
