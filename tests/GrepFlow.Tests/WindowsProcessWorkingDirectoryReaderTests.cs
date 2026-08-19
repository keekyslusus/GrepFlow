using System.Diagnostics;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class WindowsProcessWorkingDirectoryReaderTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"GrepFlow-ProcessCwd-{Guid.NewGuid():N}");

    public WindowsProcessWorkingDirectoryReaderTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [Fact]
    public void CurrentProcessReturnsCurrentDirectoryOnNativeX64Windows()
    {
        Assert.True(Environment.Is64BitOperatingSystem);
        Assert.True(Environment.Is64BitProcess);
        var expected = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(Environment.CurrentDirectory);

        var actual = new WindowsProcessWorkingDirectoryReader().TryRead((uint)Environment.ProcessId);

        Assert.Equal(expected, actual, ignoreCase: true);
    }

    [Fact]
    public void InvalidProcessIdReturnsNull()
    {
        Assert.Null(new WindowsProcessWorkingDirectoryReader().TryRead(uint.MaxValue));
    }

    [Fact]
    public void ProcessExitRaceDoesNotThrow()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            ArgumentList = { "/c", "exit" },
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Assert.NotNull(process);

        var exception = Record.Exception(
            () => new WindowsProcessWorkingDirectoryReader().TryRead((uint)process.Id));
        process.WaitForExit();

        Assert.Null(exception);
    }

    [Fact]
    public void NormalizationReturnsExistingAbsoluteLocalDirectory()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "folder")).FullName;
        var value = folder + Path.DirectorySeparatorChar;

        Assert.Equal(
            folder,
            WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(value),
            ignoreCase: true);
    }

    [Theory]
    [InlineData("relative-folder")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\path\\that\\does\\not\\exist")]
    [InlineData("\0malformed")]
    public void NormalizationRejectsInvalidPaths(string value)
    {
        Assert.Null(WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(value));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
