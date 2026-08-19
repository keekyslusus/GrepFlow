using System.Runtime.InteropServices;
using GrepFlow.Interop;
using Xunit;

namespace GrepFlow.Tests;

public sealed class CursorWindowInspectorTests
{
    [Fact]
    public void GlassUsesStableClassTokenAndIgnoresGeneratedAutomationId()
    {
        var snapshot = CursorWindowInspector.Classify([
            Element("_r_1d_", "button primary project-selector__trigger trailing", "googlekeepflow", true),
        ]);

        Assert.Equal(CursorWindowMode.Glass, snapshot?.Mode);
        Assert.Equal("googlekeepflow", snapshot?.WorkspaceLabel);
    }

    [Fact]
    public void PartialGlassClassTokenDoesNotMatch()
    {
        Assert.Null(CursorWindowInspector.Classify([
            Element("", "not-project-selector__trigger-suffix", "project", true),
        ]));
    }

    [Fact]
    public void IdeUsesWorkbenchMarkerAndStatusWorkspaceLabel()
    {
        var snapshot = CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
            Element("status.workspaceName", "", " Workspace: GoogleKeepFlow ", false),
        ]);

        Assert.Equal(CursorWindowMode.Ide, snapshot?.Mode);
        Assert.Equal("GoogleKeepFlow", snapshot?.WorkspaceLabel);
    }

    [Theory]
    [InlineData("Рабочая область: Проект", "Проект")]
    [InlineData("Проект", "Проект")]
    public void IdeStatusWorkspaceLabelDoesNotRequireEnglishPrefix(string accessibleName, string expected)
    {
        var snapshot = CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
            Element("status.workspaceName", "", accessibleName, false),
        ]);

        Assert.Equal(expected, snapshot?.WorkspaceLabel);
    }

    [Fact]
    public void GlassHasPriorityDuringMixedTransitionTree()
    {
        var snapshot = CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
            Element("status.workspaceName", "", "Workspace: IDE", false),
            Element("_r_9_", "project-selector__trigger", "Glass", true),
        ]);

        Assert.Equal(CursorWindowMode.Glass, snapshot?.Mode);
        Assert.Equal("Glass", snapshot?.WorkspaceLabel);
    }

    [Fact]
    public void IdeFallsBackToUniqueVisibleExplorerSection()
    {
        var snapshot = CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
            Element("", "pane-header expanded", "Explorer Section: playground", true),
            Element("", "pane-header", "Explorer Section: hidden", true, false),
        ]);

        Assert.Equal("playground", snapshot?.WorkspaceLabel);
    }

    [Fact]
    public void IdeExplorerFallbackDoesNotRequireEnglishPrefix()
    {
        var snapshot = CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
            Element("", "pane-header expanded", "Раздел проводника: проект", true),
        ]);

        Assert.Equal("проект", snapshot?.WorkspaceLabel);
    }

    [Fact]
    public void IdeExplorerFallbackIgnoresUnrelatedVisibleColonLabels()
    {
        var snapshot = CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
            Element("", "pane-header expanded", "Explorer Section: playground", true),
            Element("", "statusbar-item", "Spaces: 4", false),
            Element("", "terminal-label", "Port: 3000", false),
        ]);

        Assert.Equal("playground", snapshot?.WorkspaceLabel);
    }

    [Fact]
    public void EmptyAndAmbiguousLabelsReturnNull()
    {
        Assert.Null(CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
        ]));
        Assert.Null(CursorWindowInspector.Classify([
            Element("_r_1_", "project-selector__trigger", "one", true),
            Element("_r_2_", "project-selector__trigger", "two", true),
        ]));
        Assert.Null(CursorWindowInspector.Classify([
            Element("workbench.parts.editor", "", "", false),
            Element("", "pane-header", "Explorer Section: one", true),
            Element("", "pane-header", "Explorer Section: two", true),
        ]));
    }

    [Fact]
    public void RecoverableExceptionDoesNotPoisonNextInspection()
    {
        var calls = 0;
        var inspector = new CursorWindowInspector(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new COMException("unavailable");
            return [Element("", "project-selector__trigger", "project", true)];
        });

        Assert.Null(inspector.TryInspect(new IntPtr(42)));
        Assert.Equal("project", inspector.TryInspect(new IntPtr(42))?.WorkspaceLabel);
    }

    private static CursorAutomationElementSnapshot Element(
        string automationId,
        string className,
        string name,
        bool isButton,
        bool visible = true)
        => new(automationId, className, name, isButton, visible);
}
