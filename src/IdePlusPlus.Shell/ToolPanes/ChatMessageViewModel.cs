using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class ChatMessageViewModel(ChatRole role) : ObservableObject
{
    public ChatRole Role { get; } = role;

    public bool IsUser => Role == ChatRole.User;

    [ObservableProperty]
    private string text = "";

    public void Append(string delta) => Text += delta;
}
