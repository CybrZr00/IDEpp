using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.ProjectSystem;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class SolutionExplorerPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "SolutionExplorer";

    public SolutionExplorerPaneViewModel()
        : base(PaneContentId, "Solution Explorer")
    {
    }

    public ObservableCollection<SolutionTreeNode> RootNodes { get; } = [];

    /// <summary>
    /// Bound two-way to the pane's "Show unloaded projects" toggle. Off, unloaded projects are hidden
    /// from the tree - collapsed through <see cref="SolutionTreeNode.IsHiddenByFilter"/>, not removed,
    /// so toggling needs no reload and a hidden node keeps its selection and expansion. A project that
    /// failed to load is never hidden: it is a problem the user has not chosen to put away.
    /// MainWindowViewModel persists the value per solution.
    /// </summary>
    [ObservableProperty]
    private bool showUnloadedProjects = true;

    partial void OnShowUnloadedProjectsChanged(bool value) => ApplyFilter();

    public void SetSolution(SolutionTreeNode solutionRoot)
    {
        RootNodes.Clear();
        RootNodes.Add(solutionRoot);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach (var node in RootNodes.SelectMany(root => root.Flatten()))
        {
            node.IsHiddenByFilter = !ShowUnloadedProjects && node is { Kind: SolutionTreeNodeKind.Project, IsUnloaded: true };
        }
    }
}
