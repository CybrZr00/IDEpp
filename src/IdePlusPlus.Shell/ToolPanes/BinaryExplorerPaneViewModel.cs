namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>The dockable Binary Explorer pane - a shared <see cref="BinaryViewerViewModel"/> on a tool
/// pane. The same viewer also backs a binary document tab.</summary>
public sealed class BinaryExplorerPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "BinaryExplorer";

    public BinaryExplorerPaneViewModel()
        : base(PaneContentId, "Binary Explorer")
    {
    }

    public BinaryViewerViewModel Viewer { get; } = new();
}
