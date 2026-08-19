using System.Diagnostics;
using GrepFlow.Search;
using GrepFlow.Settings;
using Xunit;

namespace GrepFlow.Tests;

public sealed class RipgrepSearchEngineTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, true)]
    public void ConfigureUsesCurrentSearchScopeSettings(
        bool searchIgnoredFiles,
        bool searchHiddenFiles,
        bool expectsNoIgnore,
        bool expectsHidden)
    {
        var settings = new PluginSettings();
        var engine = new RipgrepSearchEngine(
            settings,
            new RipgrepExecutable("rg.exe"),
            new RipgrepJsonParser());
        settings.SearchIgnoredFiles = searchIgnoredFiles;
        settings.SearchHiddenFiles = searchHiddenFiles;
        var startInfo = new ProcessStartInfo();

        engine.Configure(
            startInfo,
            new SearchRequest("needle", Path.GetTempPath(), RipgrepUserOptions.Default));

        var expected = new List<string>
        {
            "--json", "--no-config", "--max-filesize", "10M", "--smart-case",
        };
        if (expectsHidden) expected.Add("--hidden");
        if (expectsNoIgnore) expected.Add("--no-ignore");
        expected.AddRange(["-e", "needle", "--", "./"]);

        Assert.Equal(expected, startInfo.ArgumentList);
    }

    [Fact]
    public void ConfigureSerializesOnlyTypedOptionsAndPinsThePatternAndWorkspaceTail()
    {
        var engine = CreateEngine();
        var startInfo = new ProcessStartInfo();
        var options = RipgrepUserOptions.Default with
        {
            CaseMode = SearchCaseMode.Sensitive,
            FixedStrings = true,
            WordRegexp = true,
            LineRegexp = true,
            IncludeHidden = true,
            IncludeIgnored = true,
            Globs = new[] { "*.cs", "!generated/*" },
            CaseInsensitiveGlobs = new[] { "README *" },
            Types = new[] { "cs", "json" },
            ExcludedTypes = new[] { "html" },
        };

        engine.Configure(startInfo, new SearchRequest("-needle", @"C:\workspace", options));

        Assert.Equal(
            new[]
            {
                "--json", "--no-config", "--max-filesize", "10M", "--case-sensitive",
                "--hidden", "--no-ignore", "--fixed-strings", "--word-regexp", "--line-regexp",
                "--glob", "*.cs", "--glob", "!generated/*", "--iglob", "README *",
                "--type", "cs", "--type", "json", "--type-not", "html",
                "-e", "-needle", "--", "./",
            },
            startInfo.ArgumentList);
        Assert.Equal(@"C:\workspace", startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void ConfigureDoesNotDuplicateEffectiveScopeOptions()
    {
        var settings = new PluginSettings { SearchHiddenFiles = true, SearchIgnoredFiles = true };
        var engine = new RipgrepSearchEngine(settings, new RipgrepExecutable("rg.exe"), new RipgrepJsonParser());
        var startInfo = new ProcessStartInfo();
        var options = RipgrepUserOptions.Default with { IncludeHidden = true, IncludeIgnored = true };

        engine.Configure(startInfo, new SearchRequest("needle", @"C:\workspace", options));

        Assert.Equal(1, startInfo.ArgumentList.Count(value => value == "--hidden"));
        Assert.Equal(1, startInfo.ArgumentList.Count(value => value == "--no-ignore"));
        Assert.Equal(1, startInfo.ArgumentList.Count(value => value == "--json"));
        Assert.Equal(1, startInfo.ArgumentList.Count(value => value == "--max-filesize"));
        Assert.DoesNotContain("--pre", startInfo.ArgumentList);
        Assert.DoesNotContain("--no-json", startInfo.ArgumentList);
    }

    private static RipgrepSearchEngine CreateEngine() =>
        new(new PluginSettings(), new RipgrepExecutable("rg.exe"), new RipgrepJsonParser());
}
