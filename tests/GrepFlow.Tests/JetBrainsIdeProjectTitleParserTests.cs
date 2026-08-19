using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class JetBrainsIdeProjectTitleParserTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"GrepFlow-idea-title-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void UniqueProjectNamePlusEditorFileMatchesKnownProject()
    {
        Assert.Equal(
            "GrepFlow",
            JetBrainsIdeProjectTitleParser.MatchKnownProjectName("GrepFlow – Main.cs", ["GrepFlow"]));
    }

    [Fact]
    public void ProjectTitleWithoutEditorFileMatchesKnownProject()
    {
        Assert.Equal(
            "GrepFlow",
            JetBrainsIdeProjectTitleParser.MatchKnownProjectName("GrepFlow", ["GrepFlow"]));
    }

    [Fact]
    public void FullAbsolutePathInBracketsIsPreferred()
    {
        Assert.Equal(
            _temporaryDirectory,
            JetBrainsIdeProjectTitleParser.TryGetExplicitProjectPath(
                $"GrepFlow [{_temporaryDirectory}] – Main.cs",
                @"C:\Users\test"));
    }

    [Fact]
    public void UserHomeRelativePathInBracketsIsExpanded()
    {
        var project = Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "projects", "GrepFlow")).FullName;

        Assert.Equal(
            project,
            JetBrainsIdeProjectTitleParser.TryGetExplicitProjectPath(
                "GrepFlow [~/projects/GrepFlow] – Main.cs",
                _temporaryDirectory));
    }

    [Fact]
    public void ProjectNameContainingSeparatorStillAllowsBracketedPath()
    {
        Assert.Equal(
            _temporaryDirectory,
            JetBrainsIdeProjectTitleParser.TryGetExplicitProjectPath(
                $"my – project [{_temporaryDirectory}] – file.kt",
                @"C:\Users\test"));
    }

    [Fact]
    public void SpacesAndNonAsciiCharactersAreRetained()
    {
        const string name = "Mój projekt 例";
        Assert.Equal(name, JetBrainsIdeProjectTitleParser.MatchKnownProjectName($"{name} – plik.kt", [name]));
    }

    [Theory]
    [InlineData("my-project")]
    [InlineData("my–project")]
    [InlineData("my – project")]
    public void ProjectNamesContainingDashCharactersUseLongestKnownMatch(string name)
    {
        Assert.Equal(
            name,
            JetBrainsIdeProjectTitleParser.MatchKnownProjectName($"{name} – file.kt", ["my", name]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Project [] – file.kt")]
    [InlineData("Project [relative/path] – file.kt")]
    [InlineData("Project [\\\\server\\share] – file.kt")]
    public void EmptyOrMalformedExplicitPathsReturnNull(string? title)
    {
        Assert.Null(JetBrainsIdeProjectTitleParser.TryGetExplicitProjectPath(title, _temporaryDirectory));
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);
}
