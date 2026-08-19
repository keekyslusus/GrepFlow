using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class TerminalAccessibleTextReaderTests
{
    [Fact]
    public void SingleTextElementIsUsedWithoutSpecificFocus()
    {
        Assert.Equal(0, Select((string.Empty, null)));
    }

    [Fact]
    public void HeaderTextBlockIsIgnoredBeforeOnePaneSelection()
    {
        Assert.Equal(1, Select(("HeaderTextBlock", null), (string.Empty, null)));
    }

    [Fact]
    public void HeaderTextBlockMatchingIsCaseInsensitive()
    {
        Assert.Equal(1, Select(("headertextblock", null), (string.Empty, null)));
    }

    [Fact]
    public void SeveralTerminalTextElementsWithoutSpecificFocusFailClosed()
    {
        Assert.Null(Select((string.Empty, null), ("TerminalPane", null)));
    }

    [Fact]
    public void UnclassifiableSiblingFailsClosedInsteadOfLeavingOnePane()
    {
        Assert.Null(TerminalAccessibleTextReader.SelectCandidateIndex(
            new (string?, int?)[] { (null, null), (string.Empty, null) },
            false));
    }

    [Fact]
    public void FocusRelatedPaneIsSelectedInsteadOfLongerSibling()
    {
        Assert.Equal(1, SelectWithFocus((string.Empty, null), ("TerminalPane", 1)));
    }

    [Fact]
    public void NearestFocusRelatedTextElementWinsOverAncestor()
    {
        Assert.Equal(1, SelectWithFocus((string.Empty, 4), ("TerminalPane", 1)));
    }

    [Fact]
    public void EquallyNearFocusCandidatesFailClosed()
    {
        Assert.Null(SelectWithFocus((string.Empty, 1), ("TerminalPane", 1)));
    }

    private static int? Select(params (string? AutomationId, int? FocusDistance)[] candidates)
        => TerminalAccessibleTextReader.SelectCandidateIndex(candidates, false);

    private static int? SelectWithFocus(params (string? AutomationId, int? FocusDistance)[] candidates)
        => TerminalAccessibleTextReader.SelectCandidateIndex(candidates, true);
}
