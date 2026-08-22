using System.Collections.Specialized;
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

    private void SaveApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _boundViewModel?.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    private void SaveDeepSeekApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _boundViewModel?.SaveDeepSeekApiKey(DeepSeekApiKeyBox.Password);
        DeepSeekApiKeyBox.Clear();
    }

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
}
