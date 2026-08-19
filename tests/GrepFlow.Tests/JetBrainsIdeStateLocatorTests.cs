using System.Text.Json;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class JetBrainsIdeStateLocatorTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"GrepFlow-jetbrains-locator-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void AndroidStudioProductInfoSelectsExpectedDirectories()
    {
        var process = CreateInstall("studio64.exe", "AndroidStudio2026.1.3", 123);
        WritePid("Google", "AndroidStudio2026.1.3", 123);

        var state = CreateLocator("Google", "AndroidStudio").TryLocate(process);

        Assert.Equal(LocalState("Google", "AndroidStudio2026.1.3"), state?.SystemDirectory);
        Assert.Equal(
            Path.Combine(LocalState("Google", "AndroidStudio2026.1.3"), "log", "idea.log"),
            state?.LogPath);
        Assert.Equal(
            Path.Combine(_temporaryDirectory, "roaming", "Google", "AndroidStudio2026.1.3"),
            state?.ConfigDirectory);
    }

    [Fact]
    public void IntelliJProductInfoPreservesExpectedDirectories()
    {
        var process = CreateInstall("idea64.exe", "IntelliJIdea2026.2", 123);
        WritePid("JetBrains", "IntelliJIdea2026.2", 123);

        var state = CreateLocator("JetBrains", "IntelliJIdea").TryLocate(process);

        Assert.Equal(LocalState("JetBrains", "IntelliJIdea2026.2"), state?.SystemDirectory);
        Assert.Equal(
            Path.Combine(_temporaryDirectory, "roaming", "JetBrains", "IntelliJIdea2026.2"),
            state?.ConfigDirectory);
    }

    [Fact]
    public void MatchingPidSelectsCurrentStudioInstanceAmongImmediateFallbackCandidates()
    {
        var process = CreateInstall("studio64.exe", "MissingDefault", 222);
        WritePid("Google", "AndroidStudio2025.3", 111);
        var expected = WritePid("Google", "AndroidStudio2026.1.3", 222);
        WritePid("Google", "OtherProduct2026.1", 222);

        Assert.Equal(expected, CreateLocator("Google", "AndroidStudio").TryLocate(process)?.SystemDirectory);
    }

    [Fact]
    public void MismatchedPidIsRejected()
    {
        var process = CreateInstall("studio64.exe", "AndroidStudio2026.1.3", 222);
        WritePid("Google", "AndroidStudio2026.1.3", 111);

        Assert.Null(CreateLocator("Google", "AndroidStudio").TryLocate(process));
    }

    [Fact]
    public void FallbackDirectoryEnumerationIsImmediateOnly()
    {
        var process = CreateInstall("studio64.exe", "MissingDefault", 222);
        var nested = Path.Combine(LocalState("Google", "Wrapper"), "AndroidStudio2026.1.3");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, ".pid"), "222");

        Assert.Null(CreateLocator("Google", "AndroidStudio").TryLocate(process));
    }

    [Fact]
    public void ProductLocatorsRemainInsideConfiguredVendorAndPrefix()
    {
        var studioProcess = CreateInstall("studio64.exe", "Unavailable", 222);
        var studioState = WritePid("Google", "AndroidStudio2026.1.3", 222);
        WritePid("JetBrains", "IntelliJIdea2026.2", 222);

        Assert.Equal(
            studioState,
            CreateLocator("Google", "AndroidStudio").TryLocate(studioProcess)?.SystemDirectory);

        var ideaProcess = CreateInstall("idea64.exe", "Unavailable", 333);
        var ideaState = WritePid("JetBrains", "IntelliJIdea2026.2", 333);
        WritePid("Google", "AndroidStudio2026.1.3", 333);

        Assert.Equal(
            ideaState,
            CreateLocator("JetBrains", "IntelliJIdea").TryLocate(ideaProcess)?.SystemDirectory);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../AndroidStudio2026.1.3")]
    [InlineData("AndroidStudio/2026.1.3")]
    [InlineData("C:\\AndroidStudio2026.1.3")]
    public void UnsafeProductInfoDataDirectoryNamesAreRejected(string dataDirectoryName)
    {
        var process = CreateInstall("studio64.exe", dataDirectoryName, 222);

        Assert.Null(CreateLocator("Google", "AndroidStudio").TryLocate(process));
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private JetBrainsIdeStateLocator CreateLocator(string vendorDirectory, string stateDirectoryPrefix)
        => new(
            Path.Combine(_temporaryDirectory, "local"),
            Path.Combine(_temporaryDirectory, "roaming"),
            vendorDirectory,
            stateDirectoryPrefix);

    private JetBrainsIdeProcessWindow CreateInstall(
        string executableName,
        string dataDirectoryName,
        uint processId)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "install", "bin")).FullName;
        var imagePath = Path.Combine(bin, executableName);
        File.WriteAllText(imagePath, "");
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, "install", "product-info.json"),
            JsonSerializer.Serialize(new { dataDirectoryName }));
        return new JetBrainsIdeProcessWindow(new IntPtr(42), processId, imagePath);
    }

    private string WritePid(string vendorDirectory, string dataDirectoryName, uint processId)
    {
        var directory = Directory.CreateDirectory(LocalState(vendorDirectory, dataDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(directory, ".pid"), processId.ToString());
        return directory;
    }

    private string LocalState(string vendorDirectory, string dataDirectoryName)
        => Path.Combine(_temporaryDirectory, "local", vendorDirectory, dataDirectoryName);
}
