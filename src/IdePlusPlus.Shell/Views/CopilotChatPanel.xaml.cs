using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell.Views;

public partial class CopilotChatPanel : UserControl
{
    private CopilotChatPaneViewModel? _boundViewModel;

    public CopilotChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        PreviewKeyDown += OnPanelPreviewKeyDown;
    }

    /// <summary>
    /// Ctrl+L starts a new conversation from anywhere in the pane. Handled on the way down rather
    /// than as a KeyBinding so no child control can claim the gesture first - the rich-text editing
    /// commands bind Ctrl+L to AlignLeft on some text controls. Pane-scoped on purpose: in the code
    /// editor Ctrl+L means something else to anyone coming from Visual Studio, and pressing it there
    /// must never wipe the chat.
    /// </summary>
    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.L || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        if (_boundViewModel?.NewConversationCommand.CanExecute(null) == true)
        {
            _boundViewModel.NewConversationCommand.Execute(null);
        }
    }

    private void Rebind()
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _boundViewModel = DataContext as CopilotChatPaneViewModel;
        if (_boundViewModel is not null)
        {
            _boundViewModel.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        MessagesScrollViewer.ScrollToBottom();

    private void InputTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        if (_boundViewModel?.SendCommand.CanExecute(null) == true)
        {
            _boundViewModel.SendCommand.Execute(null);
        }
    }

    /// <summary>Copies one chat bubble's content as plain text. Shared across every bubble type
    /// rather than a RelayCommand per view model, since this is pure presentation - it needs no
    /// state the view models don't already expose as properties.</summary>
    private void CopyBubbleText_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: { } boundMessage })
        {
            return;
        }

        var text = boundMessage switch
        {
            ChatMessageViewModel message => message.Text,
            ReviewMessageViewModel review => review.Text,
            EditProposalViewModel proposal => proposal.BuildPlainTextSummary(),
            PlanProposalViewModel plan =>
                plan.Summary + "\n" + string.Join("\n", plan.Steps.Select(step => $"- {step}")),
            McpToolCallViewModel toolCall => $"{toolCall.ToolName}: {toolCall.Status}\n{toolCall.ResultPreview}",
            _ => null,
        };

        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }
}
