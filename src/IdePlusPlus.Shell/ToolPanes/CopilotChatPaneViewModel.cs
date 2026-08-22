namespace IdePlusPlus.Shell.ToolPanes;

public sealed class CopilotChatPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "CopilotChat";

    public CopilotChatPaneViewModel()
        : base(PaneContentId, "Copilot Chat")
    {
    }
}
