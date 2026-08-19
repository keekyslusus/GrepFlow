using System.Text.Json;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class JetBrainsIdeWorkspaceReaderTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"GrepFlow-jetbrains-reader-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void BracketedPathResolvesWithoutIdeState()
    {
        var reader = new JetBrainsIdeWorkspaceReader(
            CreateLocator("JetBrains", "IntelliJIdea"),
            new JetBrainsIdeLogReader(),
            _ => $"Project [{_temporaryDirectory}] – file.kt",
            _temporaryDirectory);

        Assert.Equal(_temporaryDirectory, reader.TryReadProjectFolder(Process("idea64.exe")));
    }

    [Fact]
    public void IntelliJWindowProjectNameResolvesThroughCurrentProcessLog()
    {
        var process = CreateConfiguredInstall(
            "idea64.exe",
            "JetBrains",
            "IntelliJIdea2026.2",
            "Project",
            123);
        var reader = new JetBrainsIdeWorkspaceReader(
            CreateLocator("JetBrains", "IntelliJIdea"),
            new JetBrainsIdeLogReader(),
            _ => "Project – file.kt",
            _temporaryDirectory);

        Assert.Equal(ProjectDirectory("Project"), reader.TryReadProjectFolder(process));
    }

    [Fact]
    public void AndroidStudioWindowProjectNameResolvesThroughConfirmedLogShape()
    {
        var process = CreateConfiguredInstall(
            "studio64.exe",
            "Google",
            "AndroidStudio2026.1.3",
            "windhawk_mods",
            456);
        var reader = new JetBrainsIdeWorkspaceReader(
            CreateLocator("Google", "AndroidStudio"),
            new JetBrainsIdeLogReader(),
            _ => "windhawk_mods – README.md",
            _temporaryDirectory);

        Assert.Equal(ProjectDirectory("windhawk_mods"), reader.TryReadProjectFolder(process));
    }

    [Fact]
    public void PyCharmWindowProjectNameResolvesThroughPidConfirmedLog()
    {
        var process = CreateConfiguredInstall(
            "pycharm64.exe",
            "JetBrains",
            "PyCharm2026.2",
            "highlight_plugins",
            789);
        var reader = new JetBrainsIdeWorkspaceReader(
            CreateLocator("JetBrains", "PyCharm"),
            new JetBrainsIdeLogReader(),
            _ => "highlight_plugins",
            _temporaryDirectory);

        Assert.Equal(ProjectDirectory("highlight_plugins"), reader.TryReadProjectFolder(process));
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private JetBrainsIdeStateLocator CreateLocator(string vendorDirectory, string stateDirectoryPrefix)
        => new(
            Path.Combine(_temporaryDirectory, "local"),
            Path.Combine(_temporaryDirectory, "roaming"),
            vendorDirectory,
            stateDirectoryPrefix);

    private JetBrainsIdeProcessWindow CreateConfiguredInstall(
        string executableName,
        string vendorDirectory,
        string dataDirectoryName,
        string projectName,
        uint processId)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, executableName, "bin")).FullName;
        var imagePath = Path.Combine(bin, executableName);
        File.WriteAllText(imagePath, "");
        File.WriteAllText(
            Path.Combine(Directory.GetParent(bin)!.FullName, "product-info.json"),
            JsonSerializer.Serialize(new { dataDirectoryName }));

        var system = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, "local", vendorDirectory, dataDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(system, ".pid"), processId.ToString());
        var logDirectory = Directory.CreateDirectory(Path.Combine(system, "log")).FullName;
        File.WriteAllText(
            Path.Combine(logDirectory, "idea.log"),
            $"Setting project frame to Project(name={projectName}, containerState=COMPONENT_CREATED, componentStore={ProjectDirectory(projectName)}){Environment.NewLine}");

        return new JetBrainsIdeProcessWindow(new IntPtr(42), processId, imagePath);
    }

    private string ProjectDirectory(string projectName)
        => Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "projects", projectName)).FullName;

    private static JetBrainsIdeProcessWindow Process(string executableName)
        => new(new IntPtr(42), 123, Path.Combine(@"C:\missing\bin", executableName));
}
