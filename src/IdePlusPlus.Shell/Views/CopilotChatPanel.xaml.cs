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
