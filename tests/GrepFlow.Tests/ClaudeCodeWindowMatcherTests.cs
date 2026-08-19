using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class ClaudeCodeWindowMatcherTests
{
    private readonly string _profile = @"C:\Users\tester";

    [Fact]
    public void AbsoluteWindowsPathMatches()
    {
        var candidate = @"C:\Users\tester\source\GrepFlow";

        Assert.Equal(candidate, Matcher().Match(
            "Claude Code\nWorkspace  C:\\Users\\tester\\source\\GrepFlow",
            [candidate]));
    }

    [Fact]
    public void HomeRelativePathMatches()
    {
        var candidate = @"C:\Users\tester\Desktop\vm\GrepFlow";

        Assert.Equal(candidate, Matcher().Match(
            "Claude Code\n│  ~\\Desktop\\vm\\GrepFlow  │",
            [candidate]));
    }

    [Theory]
    [InlineData("Claude Code\n~\\Desktop\\vm\\GrepFlow")]
    [InlineData("Claude Code\nC:\\Users\\tester\\Desktop\\vm\\GrepFlow")]
    public void BoundedMarkerAndPathProvideStrongHeaderEvidence(string text)
    {
        Assert.True(Matcher().HasHeaderEvidence(text));
    }

    [Theory]
    [InlineData("Claude Code\nordinary text")]
    [InlineData("Command Prompt\n~\\Desktop\\vm\\GrepFlow")]
    public void HeaderEvidenceRequiresMarkerAndPathShape(string text)
    {
        Assert.False(Matcher().HasHeaderEvidence(text));
    }

    [Fact]
    public void HeaderEvidenceDoesNotUsePathOutsideBoundedHeader()
    {
        var text = string.Join(
            '\n',
            new[] { "Claude Code" }
                .Concat(Enumerable.Range(1, 19).Select(index => $"header {index}"))
                .Append(@"~\Desktop\vm\GrepFlow"));

        Assert.False(Matcher().HasHeaderEvidence(text));
    }

    [Fact]
    public void SlashAndCaseVariationsMatch()
    {
        var candidate = @"C:\Users\tester\Desktop\GrepFlow";

        Assert.Equal(candidate, Matcher().Match(
            "CLAUDE CODE\n~/desktop/grepflow",
            [candidate]));
    }

    [Theory]
    [InlineData("first", @"C:\Users\tester\first")]
    [InlineData("second", @"C:\Users\tester\second")]
    public void IdenticalProductHeadersUseDifferentWorkspacePaths(string headerFolder, string expected)
    {
        var text = $"Claude Code\n~/{headerFolder}";

        Assert.Equal(expected, Matcher().Match(
            text,
            [@"C:\Users\tester\first", @"C:\Users\tester\second"]));
    }

    [Fact]
    public void SameBasenameUsesFullHomeRelativePath()
    {
        var expected = @"C:\Users\tester\one\repo";

        Assert.Equal(expected, Matcher().Match(
            "Claude Code\n~\\one\\repo",
            [expected, @"C:\Users\tester\two\repo"]));
    }

    [Fact]
    public void TruncatedSameBasenameIsAmbiguous()
    {
        Assert.Null(Matcher().Match(
            "Claude Code\n│ …\\repo │",
            [@"C:\Users\tester\one\repo", @"C:\Users\tester\two\repo"]));
    }

    [Fact]
    public void UniqueBasenameCanMatchTruncatedPathShapedHeader()
    {
        var expected = @"C:\Users\tester\one\repo";

        Assert.Equal(expected, Matcher().Match(
            "Claude Code\n│ …\\repo │",
            [expected]));
    }

    [Fact]
    public void ConversationPathOutsideLeadingHeaderDoesNotMatch()
    {
        var lines = new[] { "Claude Code" }
            .Concat(Enumerable.Range(1, 19).Select(index => $"header {index}"))
            .Append(@"Please inspect C:\Users\tester\repo");

        Assert.Null(Matcher().Match(string.Join('\n', lines), [@"C:\Users\tester\repo"]));
    }

    [Theory]
    [InlineData("Command Prompt\nC:\\Users\\tester\\repo>")]
    [InlineData("Codex CLI\nC:\\Users\\tester\\repo")]
    [InlineData("Claude Code\nordinary conversation mentioning repo")]
    public void NonClaudeHeaderTextDoesNotMatch(string text)
    {
        Assert.Null(Matcher().Match(text, [@"C:\Users\tester\repo"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0Claude Code\n~\\repo")]
    public void MalformedOrEmptyInputFailsSafely(string? text)
    {
        Assert.Null(Matcher().Match(text, [@"C:\Users\tester\repo"]));
    }

    [Fact]
    public void OversizedInputFailsSafely()
    {
        Assert.Null(Matcher().Match(
            "Claude Code\n" + new string('x', 33 * 1024),
            [@"C:\Users\tester\repo"]));
    }

    private ClaudeCodeWindowMatcher Matcher() => new(_profile);
}
