using System.Diagnostics;
using Flow.Launcher.Plugin;
using GrepFlow.Interop;
using GrepFlow.Presentation;
using GrepFlow.Search;
using Xunit;

namespace GrepFlow.Tests;

public sealed class TerminalOpeningTests
{
    [Fact]
    public void TerminalLauncherUsesInjectedShellAndWorkingDirectoryWithoutArguments()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new TerminalLauncher(processes, @"C:\Windows\System32\cmd.exe");
        var directory = @"C:\path with spaces\source";

        launcher.Open(directory);

        Assert.NotNull(processes.StartInfo);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", processes.StartInfo.FileName);
        Assert.Equal(directory, processes.StartInfo.WorkingDirectory);
        Assert.False(processes.StartInfo.UseShellExecute);
        Assert.False(processes.StartInfo.CreateNoWindow);
        Assert.Empty(processes.StartInfo.ArgumentList);
        Assert.Empty(processes.StartInfo.Arguments);
    }

    [Fact]
    public void StatusResultStoresTheExactSearchFolderContext()
    {
        var factory = CreateResultFactory();
        var directory = @"X:\downloads\Flow-Launcher-explorer";

        var result = factory.CreateStatus("title", directory, directory);

        var context = Assert.IsType<SearchFolderContext>(result.ContextData);
        Assert.Equal(directory, context.FolderPath);
    }

    [Fact]
    public void StatusResultWithoutFolderHasNoContext()
    {
        var result = CreateResultFactory().CreateStatus("title", "subtitle", null);

        Assert.Null(result.ContextData);
    }

    [Fact]
    public void MatchTerminalMenuTargetsContainingDirectory()
    {
        var terminal = new RecordingTerminalLauncher();
        var provider = CreateContextMenuProvider(terminal);
        var match = new RipgrepMatch(
            @"C:\path with spaces\nested\file.cs",
            @"nested\file.cs",
            12,
            "matched line",
            0,
            7);

        var menus = provider.LoadContextMenus(new Result { ContextData = match });

        Assert.Equal(4, menus.Count);
        Assert.Equal(TextKeys.PluginGrepflowOpenTerminalHere, menus[1].Title);
        Assert.Equal(@"C:\path with spaces\nested", menus[1].SubTitle);
        Assert.True(menus[1].Action!(null!));
        Assert.Equal(@"C:\path with spaces\nested", terminal.WorkingDirectory);
    }

    [Fact]
    public void SearchFolderTerminalMenuTargetsCapturedFolder()
    {
        var terminal = new RecordingTerminalLauncher();
        var host = new RecordingResultActionHost();
        var provider = CreateContextMenuProvider(terminal, host.Host);
        var directory = @"X:\captured search root";

        var menus = provider.LoadContextMenus(new Result
        {
            ContextData = new SearchFolderContext(directory),
        });

        Assert.Equal(3, menus.Count);
        Assert.Equal(
            [
                TextKeys.PluginGrepflowRevealInExplorer,
                TextKeys.PluginGrepflowOpenTerminalHere,
                TextKeys.PluginGrepflowCopyFullPath,
            ],
            menus.Select(menu => menu.Title));
        Assert.All(menus, menu => Assert.Equal(directory, menu.SubTitle));

        Assert.True(menus[0].Action!(null!));
        Assert.Equal(directory, host.OpenedDirectory);
        Assert.Null(host.SelectedPath);

        Assert.True(menus[1].Action!(null!));
        Assert.Equal(directory, terminal.WorkingDirectory);

        Assert.True(menus[2].Action!(null!));
        Assert.Equal(directory, host.CopiedText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownOrNullContextProducesNoMenus(bool useUnknownContext)
    {
        var provider = CreateContextMenuProvider(new RecordingTerminalLauncher());
        var result = new Result { ContextData = useUnknownContext ? new object() : null };

        Assert.Empty(provider.LoadContextMenus(result));
    }

    [Fact]
    public void TerminalLaunchFailureReturnsFalse()
    {
        var actions = CreateActions(new ThrowingTerminalLauncher());

        var result = actions.OpenTerminal(@"C:\source");

        Assert.False(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTerminalDirectoryFailsWithoutLaunching(string directory)
    {
        var terminal = new RecordingTerminalLauncher();
        var actions = CreateActions(terminal);

        Assert.False(actions.OpenTerminal(directory));
        Assert.Null(terminal.WorkingDirectory);
    }

    private static ResultFactory CreateResultFactory() =>
        new(new LineWindow(), null!, new KeyTextProvider(), null!, "app.png", "missing.png");

    private static ContextMenuProvider CreateContextMenuProvider(
        ITerminalLauncher terminalLauncher,
        ResultActionHost? host = null) =>
        new(
            CreateActions(terminalLauncher, host),
            new ContextMenuIcons("reveal.png", "terminal.png", "path.png", "line.png"),
            new KeyTextProvider());

    private static ResultActions CreateActions(
        ITerminalLauncher terminalLauncher,
        ResultActionHost? host = null) =>
        new(
            host ?? new ResultActionHost((_, _) => { }, _ => { }, (_, _) => { }),
            new KeyTextProvider(),
            new PluginLog(Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"))),
            new StubMatchOpener(),
            terminalLauncher);

    private sealed class KeyTextProvider : ITextProvider
    {
        public string Get(string key, params object?[] arguments) => key;
    }

    private sealed class StubMatchOpener : IMatchOpener
    {
        public MatchOpenOutcome Open(RipgrepMatch match) => MatchOpenOutcome.Opened;
    }

    private sealed class RecordingProcessStarter : IProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public void Start(ProcessStartInfo startInfo) => StartInfo = startInfo;
    }

    private sealed class RecordingTerminalLauncher : ITerminalLauncher
    {
        public string? WorkingDirectory { get; private set; }

        public void Open(string workingDirectory) => WorkingDirectory = workingDirectory;
    }

    private sealed class ThrowingTerminalLauncher : ITerminalLauncher
    {
        public void Open(string workingDirectory) => throw new InvalidOperationException("launch failed");
    }

    private sealed class RecordingResultActionHost
    {
        public RecordingResultActionHost()
        {
            Host = new ResultActionHost(OpenDirectory, CopyToClipboard, (_, _) => { });
        }

        public ResultActionHost Host { get; }

        public string? OpenedDirectory { get; private set; }

        public string? SelectedPath { get; private set; }

        public string? CopiedText { get; private set; }

        private void OpenDirectory(string directory, string? selectedPath)
        {
            OpenedDirectory = directory;
            SelectedPath = selectedPath;
        }

        private void CopyToClipboard(string text) => CopiedText = text;
    }
}
