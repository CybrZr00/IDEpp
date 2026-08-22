using System.Windows;
using IdePlusPlus.Shell;

namespace IdePlusPlus.Shell.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void SaveApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    private void SaveDeepSeekApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveDeepSeekApiKey(DeepSeekApiKeyBox.Password);
        DeepSeekApiKeyBox.Clear();
    }

    private async void TestDeepSeekConnectionButton_OnClick(object sender, RoutedEventArgs e) =>
        await _viewModel.TestDeepSeekConnectionAsync(DeepSeekApiKeyBox.Password);

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
