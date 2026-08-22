using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class ChatMessageViewModel(ChatRole role) : ObservableObject
{
    public ChatRole Role { get; } = role;

    public bool IsUser => Role == ChatRole.User;

    [ObservableProperty]
    private string text = "";

    /// <summary>The model's reasoning/thinking trace for this message, if the provider streamed
    /// one - shown separately from Text in the UI, never persisted or replayed into request history.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning))]
    private string reasoningText = "";

    public bool HasReasoning => ReasoningText.Length > 0;

    public void Append(string delta) => Text += delta;

    public void AppendReasoning(string delta) => ReasoningText += delta;
}
