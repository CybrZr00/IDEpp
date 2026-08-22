using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

public sealed class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(
        SolutionExplorerPaneViewModel solutionExplorer,
        OutputPaneViewModel output,
        CopilotChatPaneViewModel copilotChat,
        WelcomeDocumentViewModel welcome)
    {
        SolutionExplorer = solutionExplorer;
        Output = output;
        CopilotChat = copilotChat;
        Welcome = welcome;
    }

    public SolutionExplorerPaneViewModel SolutionExplorer { get; }

    public OutputPaneViewModel Output { get; }

    public CopilotChatPaneViewModel CopilotChat { get; }

    public WelcomeDocumentViewModel Welcome { get; }
}
