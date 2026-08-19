using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace GrepFlow.Interop;

public enum CursorWindowMode
{
    Glass,
    Ide,
}

public sealed record CursorWindowSnapshot(CursorWindowMode Mode, string WorkspaceLabel);

internal sealed record CursorAutomationElementSnapshot(
    string AutomationId,
    string ClassName,
    string Name,
    bool IsButton,
    bool IsVisible);

public sealed class CursorWindowInspector
{
    private const string GlassClassToken = "project-selector__trigger";
    private const string IdeEditorAutomationId = "workbench.parts.editor";
    private const string IdeWorkspaceAutomationId = "status.workspaceName";
    private const string IdePaneHeaderClassToken = "pane-header";

    private readonly Func<IntPtr, IReadOnlyList<CursorAutomationElementSnapshot>?> _readElements;

    public CursorWindowInspector()
        : this(ReadElements)
    {
    }

    internal CursorWindowInspector(
        Func<IntPtr, IReadOnlyList<CursorAutomationElementSnapshot>?> readElements)
    {
        _readElements = readElements;
    }

    public CursorWindowSnapshot? TryInspect(IntPtr window)
    {
        try
        {
            var elements = _readElements(window);
            return elements is null ? null : Classify(elements);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static CursorWindowSnapshot? Classify(
        IReadOnlyList<CursorAutomationElementSnapshot> elements)
    {
        var glassMarkers = elements
            .Where(element =>
                element.IsButton &&
                ContainsCssToken(element.ClassName, GlassClassToken))
            .ToArray();

        if (glassMarkers.Length > 0)
        {
            var label = UniqueNonEmptyLabel(glassMarkers.Select(element => element.Name));
            return label is null ? null : new CursorWindowSnapshot(CursorWindowMode.Glass, label);
        }

        if (!elements.Any(element =>
                string.Equals(
                    element.AutomationId,
                    IdeEditorAutomationId,
                    StringComparison.Ordinal)))
            return null;

        var statusLabels = elements
            .Where(element =>
                element.IsVisible &&
                string.Equals(
                    element.AutomationId,
                    IdeWorkspaceAutomationId,
                    StringComparison.Ordinal))
            .Select(element => ExtractStatusLabel(element.Name))
            .Where(label => label is not null)
            .Select(label => label!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        if (statusLabels.Length > 1) return null;
        if (statusLabels.Length == 1)
            return new CursorWindowSnapshot(CursorWindowMode.Ide, statusLabels[0]);

        var explorerLabels = elements
            .Where(element =>
                element.IsVisible &&
                element.IsButton &&
                ContainsCssToken(element.ClassName, IdePaneHeaderClassToken))
            .Select(element => ExtractColonSuffix(element.Name))
            .Where(label => label is not null);
        var explorerLabel = UniqueNonEmptyLabel(explorerLabels);
        return explorerLabel is null
            ? null
            : new CursorWindowSnapshot(CursorWindowMode.Ide, explorerLabel);
    }

    private static bool ContainsCssToken(string value, string token)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(candidate => string.Equals(candidate, token, StringComparison.Ordinal));

    private static string? ExtractStatusLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        var label = separator < 0 ? trimmed : trimmed[(separator + 1)..].Trim();
        return label.Length == 0 ? null : label;
    }

    private static string? ExtractColonSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator < 0) return null;

        var label = trimmed[(separator + 1)..].Trim();
        return label.Length == 0 ? null : label;
    }

    private static string? UniqueNonEmptyLabel(IEnumerable<string?> labels)
    {
        var distinct = labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static IReadOnlyList<CursorAutomationElementSnapshot>? ReadElements(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return null;

        var root = AutomationElement.FromHandle(window);
        if (root is null) return null;

        var found = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var elements = new List<CursorAutomationElementSnapshot>(found.Count);
        foreach (AutomationElement element in found)
        {
            var current = element.Current;
            elements.Add(new CursorAutomationElementSnapshot(
                current.AutomationId ?? string.Empty,
                current.ClassName ?? string.Empty,
                current.Name ?? string.Empty,
                current.ControlType == ControlType.Button,
                !current.IsOffscreen));
        }

        return elements;
    }
}
