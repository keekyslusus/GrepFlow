using GrepFlow.Presentation;
using GrepFlow.Search;
using GrepFlow.Settings;
using Xunit;

namespace GrepFlow.Tests;

public sealed class HintPickerSettingsTests
{
    [Fact]
    public void HintsAreEnabledByDefault()
    {
        var picker = new HintPicker(new PluginSettings());

        var hint = picker.Pick(
            new HintContext(0, false, false, RipgrepUserOptions.Default, "needle", false),
            new KeyTextProvider());

        Assert.NotNull(hint);
    }

    [Fact]
    public void DisabledHintsAreNotPicked()
    {
        var picker = new HintPicker(new PluginSettings { ShowHints = false });

        var hint = picker.Pick(
            new HintContext(0, false, false, RipgrepUserOptions.Default, "needle", false),
            new KeyTextProvider());

        Assert.Null(hint);
    }

    [Fact]
    public void ZeroMatchesSkipsSearchScopeHintsEnabledInSettings()
    {
        var picker = new HintPicker(new PluginSettings
        {
            SearchIgnoredFiles = true,
            SearchHiddenFiles = true,
        });

        var hint = picker.Pick(
            new HintContext(0, false, false, RipgrepUserOptions.Default, "needle", false),
            new KeyTextProvider());

        Assert.Null(hint);
    }

    [Fact]
    public void TypedScopeOptionsSuppressTheirHints()
    {
        var picker = new HintPicker(new PluginSettings());
        var options = RipgrepUserOptions.Default with
        {
            IncludeHidden = true,
            IncludeIgnored = true,
        };

        var hint = picker.Pick(
            new HintContext(0, false, false, options, "needle", false),
            new KeyTextProvider());

        Assert.Null(hint);
    }

    [Theory]
    [InlineData("glob", TextKeys.PluginGrepflowHintTypeCs)]
    [InlineData("type", TextKeys.PluginGrepflowHintGlobCs)]
    public void LimitHintUsesTypedOptions(string option, string expectedHint)
    {
        var options = option == "glob"
            ? RipgrepUserOptions.Default with { Globs = new[] { "*.cs" } }
            : RipgrepUserOptions.Default with { Types = new[] { "cs" } };

        var hint = new HintPicker(new PluginSettings()).Pick(
            new HintContext(20, true, false, options, "needle", false),
            new KeyTextProvider());

        Assert.Equal(expectedHint, hint);
    }

    [Fact]
    public void FixedStringOptionSuppressesRegexFailureHint()
    {
        var options = RipgrepUserOptions.Default with { FixedStrings = true };

        var hint = new HintPicker(new PluginSettings()).Pick(
            new HintContext(0, false, false, options, "[", true),
            new KeyTextProvider());

        Assert.Null(hint);
    }

    private sealed class KeyTextProvider : ITextProvider
    {
        public string Get(string key, params object?[] arguments) => key;
    }
}
