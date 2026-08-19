using GrepFlow.Search;
using Xunit;

namespace GrepFlow.Tests;

public sealed class QueryTextParserTests
{
    private readonly QueryTextParser _parser = new();

    [Theory]
    [InlineData("", "")]
    [InlineData("   needle   ", "needle")]
    [InlineData("needle -- ", "needle")]
    public void QueryWithoutOptionsUsesDefaults(string text, string expectedPattern)
    {
        var parsed = _parser.Parse(text);

        Assert.Equal(expectedPattern, parsed.Pattern);
        Assert.Equal(RipgrepUserOptions.Default, parsed.UserOptions);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-F")]
    [InlineData("--fixed-strings")]
    public void FixedStringSpellingsAreAccepted(string option)
    {
        var parsed = Parse(option);

        Assert.True(parsed.UserOptions.FixedStrings);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-w", true, false)]
    [InlineData("--word-regexp", true, false)]
    [InlineData("-x", false, true)]
    [InlineData("--line-regexp", false, true)]
    public void MatchBoundarySpellingsAreAccepted(string option, bool word, bool line)
    {
        var parsed = Parse(option);

        Assert.Equal(word, parsed.UserOptions.WordRegexp);
        Assert.Equal(line, parsed.UserOptions.LineRegexp);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-i", SearchCaseMode.Ignore)]
    [InlineData("--ignore-case", SearchCaseMode.Ignore)]
    [InlineData("-s", SearchCaseMode.Sensitive)]
    [InlineData("--case-sensitive", SearchCaseMode.Sensitive)]
    [InlineData("-S", SearchCaseMode.Smart)]
    [InlineData("--smart-case", SearchCaseMode.Smart)]
    [InlineData("-s -S -i", SearchCaseMode.Ignore)]
    public void LastCaseOptionWins(string options, SearchCaseMode expected)
    {
        var parsed = Parse(options);

        Assert.Equal(expected, parsed.UserOptions.CaseMode);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-g *.cs")]
    [InlineData("-g*.cs")]
    [InlineData("--glob *.cs")]
    [InlineData("--glob=*.cs")]
    public void GlobSpellingsAreEquivalent(string option)
    {
        var parsed = Parse(option);

        Assert.Equal(new[] { "*.cs" }, parsed.UserOptions.Globs);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-t cs")]
    [InlineData("-tcs")]
    [InlineData("--type cs")]
    [InlineData("--type=cs")]
    public void TypeSpellingsAreEquivalent(string option)
    {
        var parsed = Parse(option);

        Assert.Equal(new[] { "cs" }, parsed.UserOptions.Types);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-T html")]
    [InlineData("-Thtml")]
    [InlineData("--type-not html")]
    [InlineData("--type-not=html")]
    public void ExcludedTypeSpellingsAreEquivalent(string option)
    {
        var parsed = Parse(option);

        Assert.Equal(new[] { "html" }, parsed.UserOptions.ExcludedTypes);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void QuotedAndRepeatedValuesPreserveContentAndOrder()
    {
        var parsed = Parse("-g '*. generated.cs' --glob=\"!other files/*\" --iglob 'README *' -t cs -tjson -T html");

        Assert.Equal(new[] { "*. generated.cs", "!other files/*" }, parsed.UserOptions.Globs);
        Assert.Equal(new[] { "README *" }, parsed.UserOptions.CaseInsensitiveGlobs);
        Assert.Equal(new[] { "cs", "json" }, parsed.UserOptions.Types);
        Assert.Equal(new[] { "html" }, parsed.UserOptions.ExcludedTypes);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void ScopeOptionsAreAccepted()
    {
        var parsed = Parse("--hidden --no-ignore");

        Assert.True(parsed.UserOptions.IncludeHidden);
        Assert.True(parsed.UserOptions.IncludeIgnored);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-g")]
    [InlineData("--glob")]
    [InlineData("--glob=")]
    [InlineData("--iglob")]
    [InlineData("--iglob=")]
    [InlineData("-t")]
    [InlineData("--type=")]
    [InlineData("-T")]
    [InlineData("--type-not=")]
    public void MissingValuesReturnStructuredError(string option)
    {
        var parsed = Parse(option);

        Assert.Equal(QueryParseErrorKind.MissingOptionValue, parsed.Error?.Kind);
        Assert.Equal(RipgrepUserOptions.Default, parsed.UserOptions);
    }

    [Theory]
    [InlineData("-g --hidden", "-g")]
    [InlineData("--glob -foo", "--glob")]
    [InlineData("--iglob --no-ignore", "--iglob")]
    [InlineData("-t -F", "-t")]
    [InlineData("-T -w", "-T")]
    public void SeparateValueCannotConsumeAnotherOption(string options, string expectedOption)
    {
        var parsed = Parse(options);

        Assert.Equal(QueryParseErrorKind.MissingOptionValue, parsed.Error?.Kind);
        Assert.Equal(expectedOption, parsed.Error?.Token);
        Assert.Equal(RipgrepUserOptions.Default, parsed.UserOptions);
    }

    [Theory]
    [InlineData("-g-foo")]
    [InlineData("--glob=-foo")]
    public void LeadingDashGlobIsAcceptedWhenAttached(string option)
    {
        var parsed = Parse(option);

        Assert.Equal(new[] { "-foo" }, parsed.UserOptions.Globs);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("-g '*.cs")]
    [InlineData("--glob \"*.cs")]
    public void UnterminatedQuotesReturnStructuredError(string option)
    {
        var parsed = Parse(option);

        Assert.Equal(QueryParseErrorKind.UnterminatedQuote, parsed.Error?.Kind);
    }

    [Theory]
    [InlineData("--pre powershell.exe")]
    [InlineData("--pre-glob *.ps1")]
    [InlineData("--hostname-bin cmd.exe")]
    [InlineData("--search-zip")]
    [InlineData("-z")]
    [InlineData("-f patterns.txt")]
    [InlineData("--file patterns.txt")]
    [InlineData("--ignore-file ignore.txt")]
    [InlineData("--")]
    [InlineData("--json")]
    [InlineData("--no-json")]
    [InlineData("--files")]
    [InlineData("--type-list")]
    [InlineData("--generate man")]
    [InlineData("--replace value")]
    [InlineData("--max-filesize 1G")]
    [InlineData("--no-max-filesize")]
    [InlineData("--threads 99")]
    [InlineData("--mmap")]
    [InlineData("--trace")]
    [InlineData("--debug")]
    [InlineData("-P")]
    [InlineData("--pcre2")]
    [InlineData("--engine pcre2")]
    [InlineData("--auto-hybrid-regex")]
    [InlineData("-u")]
    [InlineData("--unrestricted")]
    [InlineData("--unknown")]
    [InlineData("-q")]
    public void UnsafeAndUnknownOptionsAreRejected(string options)
    {
        var parsed = Parse(options);

        Assert.Equal(QueryParseErrorKind.UnsupportedOption, parsed.Error?.Kind);
        Assert.Equal(RipgrepUserOptions.Default, parsed.UserOptions);
    }

    [Theory]
    [InlineData("./other")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("second-workspace")]
    public void PositionalPathsAreRejected(string path)
    {
        var parsed = Parse(path);

        Assert.Equal(QueryParseErrorKind.UnexpectedPositionalArgument, parsed.Error?.Kind);
        Assert.Equal(path, parsed.Error?.Token);
    }

    private ParsedQuery Parse(string options) => _parser.Parse($"needle -- {options}");
}
