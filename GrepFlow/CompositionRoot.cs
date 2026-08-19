using System.IO;
using System.Windows.Controls;
using Flow.Launcher.Plugin;
using GrepFlow.Interop;
using GrepFlow.Presentation;
using GrepFlow.Search;
using GrepFlow.Settings;

namespace GrepFlow;

public static class CompositionRoot
{
    private const int MaxResults = 100;
    private const int MinPatternLength = 3;
    private const int DebounceMilliseconds = 180;

    public static ITextProvider CreateTextProvider(PluginInitContext context) => new FlowLauncherTextProvider(context.API.GetTranslation);

    public static PluginRuntime Create(PluginInitContext context, ITextProvider texts)
    {
        var api = context.API;
        var pluginDirectory = context.CurrentPluginMetadata.PluginDirectory;

        var log = new PluginLog(pluginDirectory);
        var settings = api.LoadSettingJsonStorage<PluginSettings>();

        var dataRoot = api.GetDataDirectory();
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = null;

        var locator = new RipgrepLocator(pluginDirectory, dataRoot);
        var executable = new RipgrepExecutable(locator.Locate());
        log.Info(
            nameof(CompositionRoot),
            executable.Path is null ? "ripgrep was not found" : $"ripgrep resolved to {executable.Path}");
        if (dataRoot is not null)
            log.Info(nameof(CompositionRoot), $"Flow data directory: {dataRoot}");

        var options = new RipgrepOptions(
            MaxResults,
            MinPatternLength,
            DebounceMilliseconds);

        var dispatcher = new StaDispatcher();
        var explorerHwnd = new ExplorerHwndCache();
        var totalCommander = new TotalCommanderWorkspaceReader(dispatcher, log);
        var visualStudioDte = new VisualStudioDteWorkspaceReader();
        var intelliJIdeaProfile = new JetBrainsIdeProfile(
            "intellij-idea",
            "IntelliJ IDEA",
            ["idea.exe", "idea64.exe"],
            "JetBrains",
            "IntelliJIdea");
        var pyCharmProfile = new JetBrainsIdeProfile(
            "pycharm",
            "PyCharm",
            ["pycharm.exe", "pycharm64.exe"],
            "JetBrains",
            "PyCharm");
        var androidStudioProfile = new JetBrainsIdeProfile(
            "android-studio",
            "Android Studio",
            ["studio64.exe"],
            "Google",
            "AndroidStudio");
        var jetBrainsLog = new JetBrainsIdeLogReader(log);
        var intelliJIdea = new JetBrainsIdeWorkspaceReader(
            new JetBrainsIdeStateLocator(
                intelliJIdeaProfile.VendorDirectory,
                intelliJIdeaProfile.StateDirectoryPrefix,
                log),
            jetBrainsLog);
        var pyCharm = new JetBrainsIdeWorkspaceReader(
            new JetBrainsIdeStateLocator(
                pyCharmProfile.VendorDirectory,
                pyCharmProfile.StateDirectoryPrefix,
                log),
            jetBrainsLog);
        var androidStudio = new JetBrainsIdeWorkspaceReader(
            new JetBrainsIdeStateLocator(
                androidStudioProfile.VendorDirectory,
                androidStudioProfile.StateDirectoryPrefix,
                log),
            jetBrainsLog);
        var codexSessions = new CodexCliSessionReader(log);
        var claudeSessions = new ClaudeCodeSessionReader(log);
        var terminalAgentResolver = new TerminalAgentForegroundResolver(
            new TerminalWindowInspector(),
            new WindowsProcessTree(),
            new TerminalAgentProcessLocator(),
            new WindowsProcessWorkingDirectoryReader(),
            codexSessions,
            claudeSessions,
            new TerminalAccessibleTextReader(),
            new CodexCodeWindowMatcher(),
            new ClaudeCodeWindowMatcher(),
            log);
        var sublimeSession = new SublimeTextSessionReader(log);
        var sublimeWorkspace = new SublimeTextWorkspaceSource(
            new SublimeTextWindowInspector(),
            sublimeSession);
        var cursorState = new CursorStateReader(log);
        var cursorWorkspace = new CursorWorkspaceSource(
            new CursorWorkspaceReader(new CursorWindowInspector(), cursorState, log));
        var zedState = new ZedStateReader(log);
        var zedWorkspace = new ZedWorkspaceSource(new ZedWindowInspector(), zedState);

        IWorkspaceSource[] sources =
        [
            new ExplorerWorkspaceSource(dispatcher, explorerHwnd),
            new FilesWorkspaceSource(new FilesWorkspaceReader(log)),
            new FilePilotWorkspaceSource(new FilePilotSessionReader(log)),
            new TotalCommanderWorkspaceSource(totalCommander),
            new VisualStudioWorkspaceSource(dispatcher, visualStudioDte),
            new JetBrainsIdeWorkspaceSource(intelliJIdeaProfile, intelliJIdea),
            new JetBrainsIdeWorkspaceSource(pyCharmProfile, pyCharm),
            new JetBrainsIdeWorkspaceSource(androidStudioProfile, androidStudio),
            new VisualStudioCodeWorkspaceSource(new VisualStudioCodeSessionReader(log)),
            cursorWorkspace,
            zedWorkspace,
            sublimeWorkspace,
            new TerminalAgentWorkspaceSource(terminalAgentResolver),
        ];

        var tracker = new ForegroundWorkspaceTracker(dispatcher, sources, explorerHwnd, log);
        tracker.Start();

        IActiveFolderProvider folders = new CompositeActiveFolderProvider(sources, tracker);

        log.Info(
            nameof(CompositionRoot),
            $"workspace sources: {string.Join(", ", sources.Select(s => s.Id))}; " +
            $"File Pilot session: {FilePilotSessionReader.DefaultSessionPath()}; " +
            $"VS Code state: {VisualStudioCodeSessionReader.DefaultStatePath()}; " +
            $"Cursor state: {CursorStateReader.DefaultStatePath()}; " +
            $"Zed state: {ZedStateReader.DefaultStatePath()}; " +
            $"Sublime Text session: {SublimeTextSessionReader.DefaultSessionPath()}; " +
            $"Codex home: {codexSessions.CodexHome}; " +
            $"Claude sessions: {claudeSessions.SessionDirectory}");

        var processStarter = new ProcessStarter();
        var comSpec = Environment.GetEnvironmentVariable("COMSPEC");
        var shellExecutable = string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
        var terminalLauncher = new TerminalLauncher(processStarter, shellExecutable);
        var matchOpener = new MatchOpener(
            new WindowsFileAssociationResolver(new WindowsAssociationExecutableQuery()),
            [
                new SublimeMatchLauncher(new FileExistence(), processStarter),
                new NotepadPlusPlusMatchLauncher(processStarter),
                new VisualStudioCodeMatchLauncher(processStarter),
            ],
            new FileOpenSafetyPolicy(),
            new ExecutableFileOpener(processStarter),
            new NotepadFileOpener(processStarter),
            new SafeFileOpener(
                new ShellFileOpener(processStarter),
                new OpenWithDialogFileOpener(new WindowsOpenWithDialogNative())));
        var resultActionHost = new ResultActionHost(
            (directory, path) => api.OpenDirectory(directory, path),
            text => api.CopyToClipboard(text),
            (title, message) => api.ShowMsgError(title, message));
        var actions = new ResultActions(resultActionHost, texts, log, matchOpener, terminalLauncher);
        var installer = new RipgrepInstaller(api, texts, executable, locator, log);
        var imagesDirectory = Path.Combine(pluginDirectory, "Images");
        var iconPath = Path.Combine(imagesDirectory, "app.png");
        var ripgrepNotFoundIconPath = Path.Combine(imagesDirectory, "ripgrep_not_found.png");
        var menuIcons = new ContextMenuIcons(
            Path.Combine(imagesDirectory, "reveal_in_explorer.png"),
            Path.Combine(imagesDirectory, "open_terminal.png"),
            Path.Combine(imagesDirectory, "copy_full_path.png"),
            Path.Combine(imagesDirectory, "copy_matched_line.png"));
        var resultFactory = new ResultFactory(new LineWindow(), actions, texts, installer, iconPath, ripgrepNotFoundIconPath);

        var coordinator = new SearchCoordinator(
            folders,
            new RipgrepSearchEngine(settings, executable, new RipgrepJsonParser()),
            new QueryTextParser(),
            resultFactory,
            texts,
            options,
            executable,
            new HintPicker(settings),
            log);

        return new PluginRuntime(
            coordinator,
            new ContextMenuProvider(actions, menuIcons, texts),
            () => new SettingsPanel(settings, texts, api.SaveSettingJsonStorage<PluginSettings>),
            tracker,
            dispatcher,
            log);
    }
}

public sealed class PluginRuntime : IDisposable
{
    private readonly ForegroundWorkspaceTracker _tracker;
    private readonly StaDispatcher _dispatcher;
    private readonly PluginLog _log;

    public PluginRuntime(
        SearchCoordinator coordinator,
        ContextMenuProvider contextMenus,
        Func<Control> createSettingPanel,
        ForegroundWorkspaceTracker tracker,
        StaDispatcher dispatcher,
        PluginLog log)
    {
        Coordinator = coordinator;
        ContextMenus = contextMenus;
        CreateSettingPanel = createSettingPanel;
        _tracker = tracker;
        _dispatcher = dispatcher;
        _log = log;
    }

    public SearchCoordinator Coordinator { get; }

    public ContextMenuProvider ContextMenus { get; }

    public Func<Control> CreateSettingPanel { get; }

    public void Dispose()
    {
        // logged so it is visible whether the host disposes the plugin on "Reload plugin data";
        // if this line never appears, every reload leaks a hook and an STA thread
        _log.Info(nameof(PluginRuntime), "disposing: unhooking WinEvent and stopping the STA thread");
        _tracker.Dispose();
        _dispatcher.Dispose();
    }
}
