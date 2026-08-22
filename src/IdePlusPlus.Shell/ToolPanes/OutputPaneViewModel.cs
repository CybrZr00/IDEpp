namespace IdePlusPlus.Shell.ToolPanes;

public sealed class OutputPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "Output";

    public OutputPaneViewModel()
        : base(PaneContentId, "Output")
    {
    }
}
