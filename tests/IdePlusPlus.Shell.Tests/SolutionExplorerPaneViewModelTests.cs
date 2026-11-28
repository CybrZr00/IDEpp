using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>The pane's "Show unloaded projects" toggle: a visibility change on the nodes, never a reload.</summary>
public sealed class SolutionExplorerPaneViewModelTests
{
    private static (SolutionTreeNode Root, SolutionTreeNode Loaded, SolutionTreeNode Unloaded, SolutionTreeNode Failed) Tree()
    {
        var root = new SolutionTreeNode("Scratch", @"C:\repo\Scratch.slnx", SolutionTreeNodeKind.Solution);
        var loaded = new SolutionTreeNode("App", @"C:\repo\App\App.csproj", SolutionTreeNodeKind.Project);
        var unloaded = new SolutionTreeNode("Mobile", @"C:\repo\Mobile\Mobile.csproj", SolutionTreeNodeKind.Project) { IsUnloaded = true };
        var failed = new SolutionTreeNode("Broken", @"C:\repo\Broken\Broken.csproj", SolutionTreeNodeKind.Project) { LoadError = "bad" };
        root.Children.Add(loaded);
        root.Children.Add(unloaded);
        root.Children.Add(failed);
        return (root, loaded, unloaded, failed);
    }

    [Fact]
    public void TurningTheToggleOff_HidesOnlyUnloadedProjects_AndOnAgainShowsThem()
    {
        var (root, loaded, unloaded, failed) = Tree();
        var pane = new SolutionExplorerPaneViewModel();
        pane.SetSolution(root);

        pane.ShowUnloadedProjects = false;

        unloaded.IsHiddenByFilter.ShouldBeTrue();
        loaded.IsHiddenByFilter.ShouldBeFalse();
        failed.IsHiddenByFilter.ShouldBeFalse("a project that failed to load is a problem to see, not one the user put away");
        root.IsHiddenByFilter.ShouldBeFalse();

        pane.ShowUnloadedProjects = true;

        unloaded.IsHiddenByFilter.ShouldBeFalse();
    }

    [Fact]
    public void ASolutionSetWhileTheToggleIsOff_ArrivesAlreadyFiltered()
    {
        var pane = new SolutionExplorerPaneViewModel { ShowUnloadedProjects = false };
        var (root, _, unloaded, _) = Tree();

        pane.SetSolution(root);

        unloaded.IsHiddenByFilter.ShouldBeTrue();
    }
}
