using System.Diagnostics;
using GrepFlow.Interop;
using GrepFlow.Search;
using Xunit;

namespace GrepFlow.Tests;

public sealed class MatchOpeningTests
{
    [Fact]
    public void SublimeDefaultLaunchesSiblingCliWithPathAndLineAsOneArgument()
    {
        var processes = new RecordingProcessStarter();
        var files = new StubFileExistence(true);
        var launcher = new SublimeMatchLauncher(files, processes);
        var match = CreateMatch(@"C:\path with spaces\README.md", 20);

        var launched = launcher.TryLaunch(@"C:\Program Files\Sublime Text\sublime_text.exe", match);

        Assert.True(launched);
        Assert.Equal(@"C:\Program Files\Sublime Text\subl.exe", processes.StartInfo!.FileName);
        Assert.False(processes.StartInfo.UseShellExecute);
        Assert.True(processes.StartInfo.CreateNoWindow);
        Assert.Equal(new[] { @"C:\path with spaces\README.md:20" }, processes.StartInfo.ArgumentList);
    }

    [Fact]
    public void DirectSublimeCliAssociationIsUsed()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new SublimeMatchLauncher(new StubFileExistence(false), processes);

        var launched = launcher.TryLaunch(@"D:\Sublime\SUBL.EXE", CreateMatch());

        Assert.True(launched);
        Assert.Equal(@"D:\Sublime\SUBL.EXE", processes.StartInfo!.FileName);
    }

    [Theory]
    [InlineData("sublime_text.exe")]
    [InlineData("SUBL.EXE")]
    public void SublimeExecutablesAreRecognizedCaseInsensitively(string fileName)
    {
        var launcher = new SublimeMatchLauncher(new StubFileExistence(false), new RecordingProcessStarter());

        Assert.True(launcher.Recognizes(Path.Combine(@"C:\Editor", fileName)));
    }

    [Theory]
    [InlineData("notepad++.exe")]
    [InlineData("NOTEPAD++.EXE")]
    public void NotepadPlusPlusExecutableIsRecognizedCaseInsensitively(string fileName)
    {
        var launcher = new NotepadPlusPlusMatchLauncher(new RecordingProcessStarter());

        Assert.True(launcher.Recognizes(Path.Combine(@"C:\Editor", fileName)));
        Assert.False(launcher.Recognizes(@"C:\Windows\notepad.exe"));
    }

    [Fact]
    public void NotepadPlusPlusUsesResolvedExecutableAndSeparateLineAndPathArguments()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new NotepadPlusPlusMatchLauncher(processes);
        var executable = @"D:\Portable Apps\Notepad++\notepad++.exe";
        var match = CreateMatch(@"C:\path with spaces\README.md", 20);

        var launched = launcher.TryLaunch(executable, match);

        Assert.True(launched);
        Assert.Equal(executable, processes.StartInfo!.FileName);
        Assert.False(processes.StartInfo.UseShellExecute);
        Assert.True(processes.StartInfo.CreateNoWindow);
        Assert.Equal(new[] { "-n20", @"C:\path with spaces\README.md" }, processes.StartInfo.ArgumentList);
    }

    [Theory]
    [InlineData("Code.exe")]
    [InlineData("CODE.EXE")]
    public void VisualStudioCodeExecutableIsRecognizedCaseInsensitively(string fileName)
    {
        var launcher = new VisualStudioCodeMatchLauncher(new RecordingProcessStarter());

        Assert.True(launcher.Recognizes(Path.Combine(@"C:\Editor", fileName)));
        Assert.False(launcher.Recognizes(@"C:\Editor\Code - Insiders.exe"));
        Assert.False(launcher.Recognizes(@"C:\Editor\codium.exe"));
    }

    [Fact]
    public void VisualStudioCodeUsesResolvedExecutableAndSeparateGotoArguments()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new VisualStudioCodeMatchLauncher(processes);
        var executable = @"D:\Portable Apps\Visual Studio Code\Code.exe";
        var match = CreateMatch(@"C:\path with spaces\README.md", 20);

        var launched = launcher.TryLaunch(executable, match);

        Assert.True(launched);
        Assert.Equal(executable, processes.StartInfo!.FileName);
        Assert.False(processes.StartInfo.UseShellExecute);
        Assert.True(processes.StartInfo.CreateNoWindow);
        Assert.Equal(
            new[] { "--reuse-window", "--goto", @"C:\path with spaces\README.md:20" },
            processes.StartInfo.ArgumentList);
    }

    [Fact]
    public void KnownEditorAssociationReturnsOpenedWithoutUsingFallback()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new VisualStudioCodeMatchLauncher(processes);
        var fallback = new RecordingFileOpener();
        var opener = CreateOpener(@"C:\Visual Studio Code\Code.exe", launcher, fallback);
        var match = CreateMatch(@"C:\source\script.cmd", 20);

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.NotNull(processes.StartInfo);
        Assert.Null(fallback.OpenedPath);
    }

    [Theory]
    [InlineData(@"C:\source\payload.exe")]
    [InlineData(@"C:\source\payload.lnk")]
    [InlineData(@"C:\source\payload.msi")]
    [InlineData(@"C:\source\payload.url")]
    public void RevealOnlyMatchAssociatedWithKnownEditorIsBlockedBeforeLaunch(string path)
    {
        var processes = new RecordingProcessStarter();
        var opener = new MatchOpener(
            new StubAssociationResolver(@"C:\Program Files\Microsoft VS Code\Code.exe"),
            [new VisualStudioCodeMatchLauncher(processes)],
            new FileOpenSafetyPolicy(),
            new ExecutableFileOpener(processes),
            new NotepadFileOpener(processes),
            new SafeFileOpener(
                new ShellFileOpener(processes),
                new RecordingFileOpener()));

        var outcome = opener.Open(CreateMatch(path, 20));

        Assert.Equal(MatchOpenOutcome.Blocked, outcome);
        Assert.Null(processes.StartInfo);
    }

    [Fact]
    public void PowerShellAssociatedScriptUsesTxtAssociatedVisualStudioCodeAtMatchedLine()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new VisualStudioCodeMatchLauncher(processes);
        var associations = new ScriptAssociationResolver(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe");
        var generic = new RecordingExecutableFileOpener();
        var notepad = new RecordingFileOpener();
        var openWith = new RecordingFileOpener();
        var opener = new MatchOpener(
            associations,
            [launcher],
            new FileOpenSafetyPolicy(),
            generic,
            notepad,
            openWith);
        var match = CreateMatch(@"C:\path with spaces\build-release.ps1", 27);

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Equal(
            new[] { "--reuse-window", "--goto", @"C:\path with spaces\build-release.ps1:27" },
            processes.StartInfo!.ArgumentList);
        Assert.Equal(new[] { match.AbsolutePath, "fallback.txt" }, associations.Paths);
        Assert.Null(generic.ExecutablePath);
        Assert.Null(notepad.OpenedPath);
        Assert.Null(openWith.OpenedPath);
    }

    [Theory]
    [InlineData(@"C:\source\build.cmd")]
    [InlineData(@"C:\source\build.BAT")]
    [InlineData(@"C:\source\app.js")]
    [InlineData(@"C:\source\module.PsM1")]
    public void TextScriptsUseSupportedTxtEditorFallback(string path)
    {
        var launcher = new SelectiveAssociatedLauncher(@"C:\Editor\safe-editor.exe");
        var associations = new ScriptAssociationResolver(
            @"C:\Unsafe\script-host.exe",
            @"C:\Editor\safe-editor.exe");
        var generic = new RecordingExecutableFileOpener();
        var notepad = new RecordingFileOpener();
        var openWith = new RecordingFileOpener();
        var opener = new MatchOpener(
            associations,
            [launcher],
            new FileOpenSafetyPolicy(),
            generic,
            notepad,
            openWith);
        var match = CreateMatch(path, 15);

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Same(match, launcher.OpenedMatch);
        Assert.Null(generic.ExecutablePath);
        Assert.Null(notepad.OpenedPath);
        Assert.Null(openWith.OpenedPath);
    }

    [Fact]
    public void DirectlyAssociatedSupportedEditorRemainsFirstChoiceForScript()
    {
        var processes = new RecordingProcessStarter();
        var associations = new ScriptAssociationResolver(
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            @"C:\Other\text-editor.exe");
        var opener = new MatchOpener(
            associations,
            [new VisualStudioCodeMatchLauncher(processes)],
            new FileOpenSafetyPolicy(),
            new RecordingExecutableFileOpener(),
            new RecordingFileOpener(),
            new RecordingFileOpener());
        var match = CreateMatch(@"C:\source\app.js", 9);

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Equal(new[] { match.AbsolutePath }, associations.Paths);
        Assert.Equal(
            new[] { "--reuse-window", "--goto", @"C:\source\app.js:9" },
            processes.StartInfo!.ArgumentList);
    }

    [Fact]
    public void MissingTxtAssociationUsesNotepadFallback()
    {
        var associations = new ScriptAssociationResolver(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            null);
        var generic = new RecordingExecutableFileOpener();
        var notepad = new RecordingFileOpener();
        var openWith = new RecordingFileOpener();
        var opener = new MatchOpener(
            associations,
            [new StubAssociatedLauncher()],
            new FileOpenSafetyPolicy(),
            generic,
            notepad,
            openWith);
        var match = CreateMatch(@"C:\source\build.ps1");

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Null(generic.ExecutablePath);
        Assert.Equal(match.AbsolutePath, notepad.OpenedPath);
        Assert.Null(openWith.OpenedPath);
    }

    [Fact]
    public void ArbitraryTxtHandlerOpensOriginalScriptWithoutShellExecution()
    {
        var processes = new RecordingProcessStarter();
        var associations = new ScriptAssociationResolver(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            @"C:\Editor\OtherEditor.exe");
        var notepad = new RecordingFileOpener();
        var openWith = new RecordingFileOpener();
        var opener = new MatchOpener(
            associations,
            [new StubAssociatedLauncher()],
            new FileOpenSafetyPolicy(),
            new ExecutableFileOpener(processes),
            notepad,
            openWith);
        var match = CreateMatch(@"C:\path with spaces\build-release.ps1", 22);

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Equal(@"C:\Editor\OtherEditor.exe", processes.StartInfo!.FileName);
        Assert.False(processes.StartInfo.UseShellExecute);
        Assert.Equal(new[] { match.AbsolutePath }, processes.StartInfo.ArgumentList);
        Assert.Null(notepad.OpenedPath);
        Assert.Null(openWith.OpenedPath);
    }

    [Fact]
    public void FailedGenericTxtHandlerUsesNotepadFallback()
    {
        var associations = new ScriptAssociationResolver(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            @"C:\Editor\OtherEditor.exe");
        var generic = new RecordingExecutableFileOpener(succeeds: false);
        var notepad = new RecordingFileOpener();
        var openWith = new RecordingFileOpener();
        var opener = new MatchOpener(
            associations,
            [new StubAssociatedLauncher()],
            new FileOpenSafetyPolicy(),
            generic,
            notepad,
            openWith);
        var match = CreateMatch(@"C:\source\build.ps1");

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Equal(@"C:\Editor\OtherEditor.exe", generic.ExecutablePath);
        Assert.Equal(match.AbsolutePath, generic.FilePath);
        Assert.Equal(match.AbsolutePath, notepad.OpenedPath);
        Assert.Null(openWith.OpenedPath);
    }

    [Fact]
    public void FailedKnownTxtLauncherDegradesToGenericOpening()
    {
        var associations = new ScriptAssociationResolver(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe");
        var generic = new RecordingExecutableFileOpener();
        var notepad = new RecordingFileOpener();
        var opener = new MatchOpener(
            associations,
            [new VisualStudioCodeMatchLauncher(new ThrowingProcessStarter())],
            new FileOpenSafetyPolicy(),
            generic,
            notepad,
            new RecordingFileOpener());
        var match = CreateMatch(@"C:\source\build.ps1", 18);

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Equal(@"C:\Program Files\Microsoft VS Code\Code.exe", generic.ExecutablePath);
        Assert.Equal(match.AbsolutePath, generic.FilePath);
        Assert.Null(notepad.OpenedPath);
    }

    [Fact]
    public void FailedCurrentEditorAttemptProceedsToTxtEditor()
    {
        var launcher = new SelectiveAssociatedLauncher(@"C:\Editor\text-editor.exe");
        var associations = new ScriptAssociationResolver(
            @"C:\Editor\failing-editor.exe",
            @"C:\Editor\text-editor.exe");
        var opener = new MatchOpener(
            associations,
            [launcher],
            new FileOpenSafetyPolicy(),
            new RecordingExecutableFileOpener(),
            new RecordingFileOpener(),
            new RecordingFileOpener());
        var match = CreateMatch(@"C:\source\build.cmd");

        var outcome = opener.Open(match);

        Assert.Equal(MatchOpenOutcome.Opened, outcome);
        Assert.Equal(
            new[] { @"C:\Editor\failing-editor.exe", @"C:\Editor\text-editor.exe" },
            launcher.AttemptedExecutables);
        Assert.Same(match, launcher.OpenedMatch);
    }

    [Fact]
    public void InvalidMatchMakesVisualStudioCodeReturnFalseAndUseShellFallback()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new VisualStudioCodeMatchLauncher(processes);
        var shell = new RecordingFileOpener();
        var opener = CreateOpener(@"C:\Visual Studio Code\Code.exe", launcher, shell);
        var match = CreateMatch(string.Empty, 0);

        Assert.False(launcher.TryLaunch(@"C:\Visual Studio Code\Code.exe", match));
        opener.Open(match);

        Assert.Null(processes.StartInfo);
        Assert.Equal(string.Empty, shell.OpenedPath);
    }

    [Fact]
    public void VisualStudioCodeLaunchExceptionReturnsFalseAndUsesShellFallback()
    {
        var launcher = new VisualStudioCodeMatchLauncher(new ThrowingProcessStarter());
        var shell = new RecordingFileOpener();
        var opener = CreateOpener(@"C:\Visual Studio Code\Code.exe", launcher, shell);
        var match = CreateMatch();

        Assert.False(launcher.TryLaunch(@"C:\Visual Studio Code\Code.exe", match));
        opener.Open(match);

        Assert.Equal(match.AbsolutePath, shell.OpenedPath);
    }

    [Fact]
    public void InvalidMatchMakesNotepadPlusPlusReturnFalseAndUseShellFallback()
    {
        var processes = new RecordingProcessStarter();
        var launcher = new NotepadPlusPlusMatchLauncher(processes);
        var shell = new RecordingFileOpener();
        var opener = CreateOpener(@"C:\Notepad++\notepad++.exe", launcher, shell);
        var match = CreateMatch(string.Empty, 0);

        Assert.False(launcher.TryLaunch(@"C:\Notepad++\notepad++.exe", match));
        opener.Open(match);

        Assert.Null(processes.StartInfo);
        Assert.Equal(string.Empty, shell.OpenedPath);
    }

    [Fact]
    public void NotepadPlusPlusLaunchExceptionReturnsFalseAndUsesShellFallback()
    {
        var launcher = new NotepadPlusPlusMatchLauncher(new ThrowingProcessStarter());
        var shell = new RecordingFileOpener();
        var opener = CreateOpener(@"C:\Notepad++\notepad++.exe", launcher, shell);
        var match = CreateMatch();

        Assert.False(launcher.TryLaunch(@"C:\Notepad++\notepad++.exe", match));
        opener.Open(match);

        Assert.Equal(match.AbsolutePath, shell.OpenedPath);
    }

    [Fact]
    public void NonSublimeHandlerUsesShellFallback()
    {
        var shell = new RecordingFileOpener();
        var opener = CreateOpener(@"C:\Windows\notepad.exe", new StubAssociatedLauncher(), shell);
        var match = CreateMatch();

        opener.Open(match);

        Assert.Equal(match.AbsolutePath, shell.OpenedPath);
    }

    [Fact]
    public void MissingAssociationUsesShellFallback()
    {
        var shell = new RecordingFileOpener();
        var opener = CreateOpener(null, new StubAssociatedLauncher(), shell);

        opener.Open(CreateMatch());

        Assert.NotNull(shell.OpenedPath);
    }

    [Fact]
    public void OpenWithDialogOutcomeIsReturnedByMatchOpener()
    {
        var safeFiles = new RecordingFileOpener(SafeFileOpenOutcome.OpenWithShown);
        var opener = CreateOpener(null, new StubAssociatedLauncher(), safeFiles);

        var outcome = opener.Open(CreateMatch());

        Assert.Equal(MatchOpenOutcome.OpenWithShown, outcome);
        Assert.NotNull(safeFiles.OpenedPath);
    }

    [Fact]
    public void MissingSiblingCliUsesShellFallback()
    {
        var shell = new RecordingFileOpener();
        var launcher = new SublimeMatchLauncher(new StubFileExistence(false), new RecordingProcessStarter());
        var opener = CreateOpener(@"C:\Sublime\sublime_text.exe", launcher, shell);

        opener.Open(CreateMatch());

        Assert.NotNull(shell.OpenedPath);
    }

    [Fact]
    public void SublimeLaunchExceptionUsesShellFallback()
    {
        var shell = new RecordingFileOpener();
        var launcher = new SublimeMatchLauncher(new StubFileExistence(true), new ThrowingProcessStarter());
        var opener = CreateOpener(@"C:\Sublime\sublime_text.exe", launcher, shell);

        opener.Open(CreateMatch());

        Assert.NotNull(shell.OpenedPath);
    }

    [Fact]
    public void InvalidMatchUsesShellFallback()
    {
        var shell = new RecordingFileOpener();
        var launcher = new SublimeMatchLauncher(new StubFileExistence(true), new RecordingProcessStarter());
        var opener = CreateOpener(@"C:\Sublime\sublime_text.exe", launcher, shell);

        opener.Open(CreateMatch(string.Empty, 0));

        Assert.Equal(string.Empty, shell.OpenedPath);
    }

    [Fact]
    public void ResolverQueriesTheMatchedFilesExtension()
    {
        var query = new RecordingAssociationQuery(@"C:\Editor\editor.exe");
        var resolver = new WindowsFileAssociationResolver(query);

        var executable = resolver.ResolveDefaultExecutable(@"C:\source\file.CS");

        Assert.Equal(@"C:\Editor\editor.exe", executable);
        Assert.Equal(".CS", query.Extension);
    }

    [Fact]
    public void ResolverReturnsNullWhenLookupFails()
    {
        var resolver = new WindowsFileAssociationResolver(new ThrowingAssociationQuery());

        Assert.Null(resolver.ResolveDefaultExecutable(@"C:\source\file.cs"));
        Assert.Null(resolver.ResolveDefaultExecutable(@"C:\source\README"));
    }

    private static MatchOpener CreateOpener(
        string? association,
        IAssociatedApplicationLauncher launcher,
        RecordingFileOpener shell) =>
        new(
            new StubAssociationResolver(association),
            [launcher],
            new FileOpenSafetyPolicy(),
            new RecordingExecutableFileOpener(),
            new RecordingFileOpener(),
            shell);

    private static RipgrepMatch CreateMatch(string path = @"C:\source\file.cs", int line = 12) =>
        new(path, "file.cs", line, "matched line", 0, 7);

    private sealed class StubAssociationResolver(string? executable) : IFileAssociationResolver
    {
        public string? ResolveDefaultExecutable(string filePath) => executable;
    }

    private sealed class StubAssociatedLauncher : IAssociatedApplicationLauncher
    {
        public bool Recognizes(string executablePath) => false;

        public bool TryLaunch(string executablePath, RipgrepMatch match) => false;
    }

    private sealed class ScriptAssociationResolver(
        string? scriptExecutable,
        string? textExecutable) : IFileAssociationResolver
    {
        public List<string> Paths { get; } = [];

        public string? ResolveDefaultExecutable(string filePath)
        {
            Paths.Add(filePath);
            return Path.GetExtension(filePath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? textExecutable
                : scriptExecutable;
        }
    }

    private sealed class SelectiveAssociatedLauncher(string successfulExecutable) : IAssociatedApplicationLauncher
    {
        public List<string> AttemptedExecutables { get; } = [];

        public RipgrepMatch? OpenedMatch { get; private set; }

        public bool Recognizes(string executablePath) => true;

        public bool TryLaunch(string executablePath, RipgrepMatch match)
        {
            AttemptedExecutables.Add(executablePath);
            if (!executablePath.Equals(successfulExecutable, StringComparison.OrdinalIgnoreCase))
                return false;

            OpenedMatch = match;
            return true;
        }
    }

    private sealed class RecordingFileOpener(
        SafeFileOpenOutcome safeOutcome = SafeFileOpenOutcome.Opened) : IFileOpener, ISafeFileOpener
    {
        public string? OpenedPath { get; private set; }

        public void Open(string path) => OpenedPath = path;

        public SafeFileOpenOutcome OpenSafe(string path)
        {
            OpenedPath = path;
            return safeOutcome;
        }
    }

    private sealed class RecordingExecutableFileOpener(bool succeeds = true) : IExecutableFileOpener
    {
        public string? ExecutablePath { get; private set; }

        public string? FilePath { get; private set; }

        public bool TryOpen(string executablePath, string filePath)
        {
            ExecutablePath = executablePath;
            FilePath = filePath;
            return succeeds;
        }
    }

    private sealed class StubFileExistence(bool exists) : IFileExistence
    {
        public bool Exists(string path) => exists;
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

    private sealed class RecordingAssociationQuery(string? executable) : IAssociationExecutableQuery
    {
        public string? Extension { get; private set; }

        public string? Query(string extension)
        {
            Extension = extension;
            return executable;
        }
    }

    private sealed class ThrowingAssociationQuery : IAssociationExecutableQuery
    {
        public string? Query(string extension) => throw new InvalidOperationException("lookup failed");
    }
}
