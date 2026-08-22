namespace IdePlusPlus.Shell.ToolPanes;

public sealed class SolutionExplorerPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "SolutionExplorer";

    public SolutionExplorerPaneViewModel()
        : base(PaneContentId, "Solution Explorer")
    {
    }
}
