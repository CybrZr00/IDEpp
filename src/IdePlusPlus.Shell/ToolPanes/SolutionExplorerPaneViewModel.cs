using System.Collections.ObjectModel;
using IdePlusPlus.ProjectSystem;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed class SolutionExplorerPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "SolutionExplorer";

    public SolutionExplorerPaneViewModel()
        : base(PaneContentId, "Solution Explorer")
    {
    }

    public ObservableCollection<SolutionTreeNode> RootNodes { get; } = [];

    public void SetSolution(SolutionTreeNode solutionRoot)
    {
        RootNodes.Clear();
        RootNodes.Add(solutionRoot);
    }
}
