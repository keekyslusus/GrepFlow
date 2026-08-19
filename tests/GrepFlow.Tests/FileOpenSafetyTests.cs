using System.Diagnostics;
using GrepFlow.Interop;
using GrepFlow.Presentation;
using GrepFlow.Search;
using Xunit;

namespace GrepFlow.Tests;

public sealed class FileOpenSafetyTests
{
    private static readonly string[] TextScriptExtensions =
    [
        ".cmd", ".bat", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".reg", ".scf",
    ];

    private static readonly string[] RevealOnlyExtensions =
    [
        ".exe", ".com", ".msi", ".msp", ".mst", ".scr", ".cpl", ".lnk", ".url",
        ".application", ".jar",
    ];

    [Fact]
    public void EveryTextScriptExtensionIsClassifiedCaseInsensitively()
    {
        var policy = new FileOpenSafetyPolicy();

        foreach (var extension in TextScriptExtensions)
        {
            Assert.True(policy.IsTextScript("file" + extension));
            Assert.True(policy.IsTextScript("file" + extension.ToUpperInvariant()));
            Assert.True(policy.IsTextScript("file" + MixedCase(extension)));
            Assert.False(policy.RequiresReveal("file" + extension));
        }
    }

    [Fact]
    public void EveryRevealOnlyExtensionIsClassifiedCaseInsensitively()
    {
        var policy = new FileOpenSafetyPolicy();

        foreach (var extension in RevealOnlyExtensions)
        {
            Assert.True(policy.RequiresReveal("file" + extension));
            Assert.True(policy.RequiresReveal("file" + extension.ToUpperInvariant()));
            Assert.True(policy.RequiresReveal("file" + MixedCase(extension)));
            Assert.False(policy.IsTextScript("file" + extension));
        }
    }

    [Theory]
    [InlineData("file.cs")]
    [InlineData("file.txt")]
    [InlineData("README")]
    public void NormalAndExtensionlessFilesAreNotClassifiedAsExecutable(string path)
    {
        var policy = new FileOpenSafetyPolicy();

        Assert.False(policy.IsTextScript(path));
        Assert.False(policy.RequiresReveal(path));
    }

    [Theory]
    [InlineData(@"C:\source\payload.exe")]
    [InlineData(@"C:\source\payload.msi")]
    [InlineData(@"C:\source\payload.lnk")]
    [InlineData(@"C:\source\payload.url")]
    public void RevealOnlyMatchIsBlockedWithoutCallingFallback(string path)
    {
        var fallback = new RecordingFileOpener();
        var opener = CreateMatchOpener(fallback);

        var outcome = opener.Open(CreateMatch(path));

        Assert.Equal(MatchOpenOutcome.Blocked, outcome);
        Assert.Null(fallback.OpenedPath);
    }

    [Theory]
    [InlineData(@"C:\source\payload.exe")]
    [InlineData(@"C:\source\payload.msi")]
    [InlineData(@"C:\source\payload.lnk")]
    [InlineData(@"C:\source\payload.url")]
    public void RevealOnlyMatchStartsNoProcess(string path)
    {
        var processes = new RecordingProcessStarter();
        var opener = new MatchOpener(
            new StubAssociationResolver(),
            [new NeverAssociatedLauncher()],
            new FileOpenSafetyPolicy(),
            new NeverExecutableFileOpener(),
            new NotepadFileOpener(processes),
            new OpenWithFileOpener(processes));

        var outcome = opener.Open(CreateMatch(path));

        Assert.Equal(MatchOpenOutcome.Blocked, outcome);
        Assert.Null(processes.StartInfo);
    }

    [Theory]
    [InlineData(@"C:\source\file.cs")]
    [InlineData(@"C:\source\notes.txt")]
    [InlineData(@"C:\source\README")]
    public void UnknownSafeMatchUsesOpenWithFallback(string path)
    {
        var fallback = new RecordingFileOpener();
        var opener = CreateMatchOpener(fallback);

        var outcome = opener.Open(CreateMatch(path));

        Assert.Equal(MatchOpenOutcome.OpenWithShown, outcome);
        Assert.Equal(path, fallback.OpenedPath);
    }

    [Theory]
    [InlineData(@"C:\path with spaces\file.txt")]
    [InlineData(@"C:\source\-leading-dash.txt")]
    [InlineData(@"C:\source\README")]
    public void OpenWithUsesExplicitVerbAndKeepsPathStructured(string path)
    {
        var processes = new RecordingProcessStarter();
        var opener = new OpenWithFileOpener(processes);

        opener.Open(path);

        Assert.NotNull(processes.StartInfo);
        Assert.Equal(path, processes.StartInfo.FileName);
        Assert.True(processes.StartInfo.UseShellExecute);
        Assert.Equal("openas", processes.StartInfo.Verb);
        Assert.Empty(processes.StartInfo.ArgumentList);
        Assert.Empty(processes.StartInfo.Arguments);
    }

    [Theory]
    [InlineData(@"C:\path with spaces\script.ps1")]
    [InlineData(@"C:\source\-leading-dash.cmd")]
    public void GenericExecutableOpenerUsesOneStructuredPathArgument(string path)
    {
        var processes = new RecordingProcessStarter();
        var opener = new ExecutableFileOpener(processes);
        var executable = @"C:\Editor\OtherEditor.exe";

        var opened = opener.TryOpen(executable, path);

        Assert.True(opened);
        Assert.NotNull(processes.StartInfo);
        Assert.Equal(executable, processes.StartInfo.FileName);
        Assert.False(processes.StartInfo.UseShellExecute);
        Assert.Equal(new[] { path }, processes.StartInfo.ArgumentList);
        Assert.Empty(processes.StartInfo.Arguments);
    }

    [Fact]
    public void GenericExecutableOpenerReturnsFalseWhenLaunchFails()
    {
        var opener = new ExecutableFileOpener(new ThrowingProcessStarter());

        Assert.False(opener.TryOpen(@"C:\Editor\OtherEditor.exe", @"C:\source\build.ps1"));
    }

    [Theory]
    [InlineData(@"C:\path with spaces\script.ps1")]
    [InlineData(@"C:\source\-leading-dash.cmd")]
    public void NotepadUsesSystemExecutableAndOneStructuredPathArgument(string path)
    {
        var processes = new RecordingProcessStarter();
        var opener = new NotepadFileOpener(processes);

        opener.Open(path);

        Assert.NotNull(processes.StartInfo);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            processes.StartInfo.FileName);
        Assert.False(processes.StartInfo.UseShellExecute);
        Assert.Equal(new[] { path }, processes.StartInfo.ArgumentList);
        Assert.Empty(processes.StartInfo.Arguments);
    }

    [Fact]
    public void BlockedResultIsRevealedAndExplainedWithoutGenericFailure()
    {
        var match = CreateMatch(@"C:\source\payload.exe");
        var host = new RecordingHost();
        var actions = new ResultActions(
            host.Host,
            new SafetyTextProvider(),
            new PluginLog(Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"))),
            new StubMatchOpener(MatchOpenOutcome.Blocked),
            new StubTerminalLauncher());

        var result = actions.OpenMatch(match);

        Assert.True(result);
        Assert.Equal(@"C:\source", host.Directory);
        Assert.Equal(match.AbsolutePath, host.SelectedPath);
        Assert.Equal("GrepFlow", host.ErrorTitle);
        Assert.Equal($"blocked: {match.AbsolutePath}", host.ErrorMessage);
    }

    [Fact]
    public void OpenedTextScriptIsNotRevealedOrReportedAsBlocked()
    {
        var match = CreateMatch(@"C:\source\build.ps1");
        var host = new RecordingHost();
        var actions = new ResultActions(
            host.Host,
            new SafetyTextProvider(),
            new PluginLog(Path.Combine(Path.GetTempPath(), "GrepFlow.Tests", Guid.NewGuid().ToString("N"))),
            new StubMatchOpener(MatchOpenOutcome.Opened),
            new StubTerminalLauncher());

        var result = actions.OpenMatch(match);

        Assert.True(result);
        Assert.Null(host.Directory);
        Assert.Null(host.SelectedPath);
        Assert.Null(host.ErrorTitle);
        Assert.Null(host.ErrorMessage);
    }

    private static MatchOpener CreateMatchOpener(IFileOpener fallback) =>
        new(
            new StubAssociationResolver(),
            [new NeverAssociatedLauncher()],
            new FileOpenSafetyPolicy(),
            new NeverExecutableFileOpener(),
            new RecordingFileOpener(),
            fallback);

    private static RipgrepMatch CreateMatch(string path) =>
        new(path, Path.GetFileName(path), 12, "matched line", 0, 7);

    private static string MixedCase(string extension)
    {
        var characters = extension.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (index % 2 == 0) characters[index] = char.ToUpperInvariant(characters[index]);
        }

        return new string(characters);
    }

    private sealed class StubAssociationResolver : IFileAssociationResolver
    {
        public string? ResolveDefaultExecutable(string filePath) => @"C:\Windows\notepad.exe";
    }

    private sealed class NeverAssociatedLauncher : IAssociatedApplicationLauncher
    {
        public bool Recognizes(string executablePath) => false;

        public bool TryLaunch(string executablePath, RipgrepMatch match) => false;
    }

    private sealed class RecordingFileOpener : IFileOpener
    {
        public string? OpenedPath { get; private set; }

        public void Open(string path) => OpenedPath = path;
    }

    private sealed class RecordingProcessStarter : IProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public void Start(ProcessStartInfo startInfo) => StartInfo = startInfo;
    }

    private sealed class ThrowingProcessStarter : IProcessStarter
    {
        public void Start(ProcessStartInfo startInfo) => throw new InvalidOperationException("launch failed");
    }

    private sealed class NeverExecutableFileOpener : IExecutableFileOpener
    {
        public bool TryOpen(string executablePath, string filePath) => false;
    }

    private sealed class StubMatchOpener(MatchOpenOutcome outcome) : IMatchOpener
    {
        public MatchOpenOutcome Open(RipgrepMatch match) => outcome;
    }

    private sealed class StubTerminalLauncher : ITerminalLauncher
    {
        public void Open(string workingDirectory)
        {
        }
    }

    private sealed class SafetyTextProvider : ITextProvider
    {
        public string Get(string key, params object?[] arguments) => key switch
        {
            TextKeys.PluginGrepflowPluginName => "GrepFlow",
            TextKeys.PluginGrepflowBlockedFileOpen => $"blocked: {arguments[0]}",
            _ => key,
        };
    }

    private sealed class RecordingHost
    {
        public RecordingHost()
        {
            Host = new ResultActionHost(OpenDirectory, _ => { }, ShowError);
        }

        public ResultActionHost Host { get; }

        public string? Directory { get; private set; }

        public string? SelectedPath { get; private set; }

        public string? ErrorTitle { get; private set; }

        public string? ErrorMessage { get; private set; }

        private void OpenDirectory(string directory, string? selectedPath)
        {
            Directory = directory;
            SelectedPath = selectedPath;
        }

        private void ShowError(string title, string message)
        {
            ErrorTitle = title;
            ErrorMessage = message;
        }
    }
}
