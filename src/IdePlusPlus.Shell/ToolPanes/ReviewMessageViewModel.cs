using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// A chat-log entry for one adversarial review - an independent second opinion on a diff, from a
/// fresh model call with no memory of the conversation that produced it (see
/// CopilotChatPaneViewModel.ReviewLastChangeAsync). Its own type, rendered distinctly from
/// ChatMessageViewModel, so it reads as a second voice looking at the work rather than the primary
/// agent talking about itself - and it is never replayed back into that agent's own history, so a
/// wrong critique is something the user weighs, not something the primary agent quietly "fixes" by
/// chasing a non-issue it never actually had.
/// </summary>
public sealed partial class ReviewMessageViewModel : ObservableObject
{
    [ObservableProperty]
    private string text = "";

    [ObservableProperty]
    private bool isComplete;

    public void Append(string delta) => Text += delta;
}
