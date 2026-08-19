using System.Windows.Automation;

namespace GrepFlow.Interop;

public sealed class TerminalAccessibleTextReader
{
    private const int MaxTextLength = 32 * 1024;

    public string? TryReadVisibleText(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;

        try
        {
            var root = AutomationElement.FromHandle(window);
            if (root is null) return null;

            var elements = root.FindAll(
                TreeScope.Element | TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            if (elements.Count == 0) return null;

            var focused = AutomationElement.FocusedElement;
            var focusIsSpecific = focused is not null &&
                                  !Automation.Compare(focused, root) &&
                                  DistanceToAncestor(focused, root) is not null;
            var candidates = new List<(
                AutomationElement Element,
                string AutomationId,
                int? FocusDistance)>();
            foreach (AutomationElement element in elements)
            {
                if (!TryReadAutomationId(element, out var automationId)) return null;
                var distance = focusIsSpecific
                    ? RelationshipDistance(focused!, element)
                    : null;
                candidates.Add((element, automationId, distance));
            }

            var selectedIndex = SelectCandidateIndex(
                candidates
                    .Select(candidate => (
                        AutomationId: (string?)candidate.AutomationId,
                        candidate.FocusDistance))
                    .ToArray(),
                focusIsSpecific);
            if (selectedIndex is null) return null;

            var selected = candidates[selectedIndex.Value].Element;
            if (!selected.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern pattern)
                return null;

            var text = ReadVisibleRanges(pattern, MaxTextLength);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    internal static int? SelectCandidateIndex(
        IReadOnlyList<(string? AutomationId, int? FocusDistance)> candidates,
        bool focusIsSpecific)
    {
        if (candidates.Any(candidate => candidate.AutomationId is null)) return null;

        var terminalCandidates = candidates
            .Select((candidate, index) => (candidate, index))
            .Where(item => !IsServiceTextElement(item.candidate.AutomationId))
            .ToArray();
        if (terminalCandidates.Length == 0) return null;

        if (focusIsSpecific)
        {
            var related = terminalCandidates
                .Where(item => item.candidate.FocusDistance is not null)
                .ToArray();
            if (related.Length > 0)
            {
                var minimum = related.Min(item => item.candidate.FocusDistance!.Value);
                var nearest = related
                    .Where(item => item.candidate.FocusDistance == minimum)
                    .Take(2)
                    .ToArray();
                return nearest.Length == 1 ? nearest[0].index : null;
            }
        }

        return terminalCandidates.Length == 1 ? terminalCandidates[0].index : null;
    }

    internal static bool IsServiceTextElement(string? automationId)
        => string.Equals(automationId, "HeaderTextBlock", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadAutomationId(AutomationElement element, out string automationId)
    {
        try
        {
            automationId = element.Current.AutomationId ?? string.Empty;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            automationId = string.Empty;
            return false;
        }
    }

    private static int? RelationshipDistance(
        AutomationElement focused,
        AutomationElement candidate)
        => DistanceToAncestor(focused, candidate) ?? DistanceToAncestor(candidate, focused);

    private static int? DistanceToAncestor(
        AutomationElement element,
        AutomationElement ancestor)
    {
        var current = element;
        for (var distance = 0; distance < 128 && current is not null; distance++)
        {
            if (Automation.Compare(current, ancestor)) return distance;
            current = TreeWalker.RawViewWalker.GetParent(current);
        }

        return null;
    }

    private static string ReadVisibleRanges(TextPattern pattern, int limit)
    {
        var text = new System.Text.StringBuilder();
        var remaining = limit;
        foreach (var range in pattern.GetVisibleRanges())
        {
            if (remaining <= 0) break;
            var value = range.GetText(remaining);
            if (string.IsNullOrEmpty(value)) continue;

            text.Append(value);
            remaining -= value.Length;
        }

        return text.ToString();
    }

    private static bool IsRecoverable(Exception exception)
        => exception is ElementNotAvailableException or InvalidOperationException or ArgumentException or
            NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or
            System.Security.SecurityException;
}
