using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class ZedWindowInspectorTests
{
    private static readonly IntPtr Hwnd = new(42);

    [Theory]
    [InlineData(@"C:\Program Files\Zed\Zed.exe")]
    [InlineData(@"C:\Program Files\Zed\ZED.EXE")]
    public void ImageNameMatchesCaseInsensitively(string path)
    {
        Assert.True(ZedWindowInspector.MatchesImageName(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Zed\Zed-preview.exe")]
    [InlineData(@"C:\Program Files\Zed\Zed.exe.bak")]
    [InlineData(null)]
    public void SimilarImageNamesAreRejected(string? path)
    {
        Assert.False(ZedWindowInspector.MatchesImageName(path));
    }

    [Theory]
    [InlineData("Zed::Window", true)]
    [InlineData("zed::window", false)]
    [InlineData("Zed::Window::Child", false)]
    [InlineData("", false)]
    public void WindowClassRequiresExactMatch(string value, bool expected)
    {
        Assert.Equal(expected, ZedWindowInspector.MatchesClassName(value));
    }

    [Fact]
    public void ValidWindowReturnsCompleteSnapshotIncludingEmptyTitle()
    {
        var inspector = Inspector(title: string.Empty);

        var snapshot = inspector.TryInspect(Hwnd);

        Assert.Equal(new ZedWindowSnapshot(
            Hwnd,
            7,
            @"C:\Zed\Zed.exe",
            string.Empty,
            "Zed::Window"), snapshot);
    }

    [Fact]
    public void ZeroInvalidAndOwnedWindowsAreRejectedBeforeLaterInspection()
    {
        var laterCalls = 0;
        var invalid = new ZedWindowInspector(
            _ => false,
            _ => IntPtr.Zero,
            _ => { laterCalls++; return @"C:\Zed\Zed.exe"; },
            _ => 7,
            _ => "Zed::Window",
            _ => "repo");
        Assert.Null(invalid.TryInspect(IntPtr.Zero));
        Assert.Null(invalid.TryInspect(Hwnd));
        Assert.Equal(0, laterCalls);

        var owned = Inspector(owner: new IntPtr(9));
        Assert.Null(owned.TryInspect(Hwnd));
    }

    [Theory]
    [InlineData(@"C:\Zed\other.exe", "Zed::Window", 7)]
    [InlineData(@"C:\Zed\Zed.exe", "OtherWindow", 7)]
    [InlineData(@"C:\Zed\Zed.exe", "Zed::Window", 0)]
    public void InvalidIdentityIsRejected(string image, string className, uint processId)
    {
        Assert.Null(Inspector(image: image, className: className, processId: processId).TryInspect(Hwnd));
    }

    private static ZedWindowInspector Inspector(
        bool isWindow = true,
        IntPtr owner = default,
        string image = @"C:\Zed\Zed.exe",
        uint processId = 7,
        string className = "Zed::Window",
        string title = "repo")
        => new(
            _ => isWindow,
            _ => owner,
            _ => image,
            _ => processId,
            _ => className,
            _ => title);
}
