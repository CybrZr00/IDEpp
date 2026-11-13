using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using IdePlusPlus.Shell;
using Microsoft.Win32;

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

    private void SaveAnthropicApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveAnthropicApiKey(AnthropicApiKeyBox.Password);
        AnthropicApiKeyBox.Clear();
    }

    private async void TestAnthropicConnectionButton_OnClick(object sender, RoutedEventArgs e) =>
        await _viewModel.TestAnthropicConnectionAsync(AnthropicApiKeyBox.Password);

    private void SaveLmStudioApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveLmStudioApiKey(LmStudioApiKeyBox.Password);
        LmStudioApiKeyBox.Clear();
    }

    private async void TestDeepSeekConnectionButton_OnClick(object sender, RoutedEventArgs e) =>
        await _viewModel.TestDeepSeekConnectionAsync(DeepSeekApiKeyBox.Password);

    /// <summary>Installs one reverse-engineering toolchain after the user approves the download. The
    /// consent dialog is shown from here (not the view model) because it needs this window as its
    /// owner. The download is verified against a pinned checksum inside the store.</summary>
    private async void InstallTool_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ToolRowViewModel row })
        {
            return;
        }

        var tool = row.Tool;
        var approved = ApprovalDialog.Ask(
            this,
            $"Install {tool.DisplayName}?",
            $"IDE++ will download {tool.DisplayName} {tool.Version} (about {tool.DownloadBytes / 1_000_000} MB) and "
            + "verify it against a pinned checksum before using it.",
            tool.DownloadUrl,
            warning: "This downloads and runs third-party software. Only continue if you trust the source above.");
        if (!approved)
        {
            return;
        }

        row.IsBusy = true;
        row.Progress = "Starting...";
        try
        {
            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IDE++");
            await _viewModel.ToolchainStore.InstallAsync(
                tool.Id,
                httpClient,
                message => Dispatcher.Invoke(() => row.Progress = message),
                CancellationToken.None);
            row.Progress = null;
        }
        catch (Exception ex)
        {
            row.Progress = $"Failed: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            _viewModel.RefreshToolchains();
        }
    }

    private void BrowseUltraWorkFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Folder for Ultra team worktrees and build output",
            InitialDirectory = Directory.Exists(_viewModel.UltraWorkFolder) ? _viewModel.UltraWorkFolder : null,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.UltraWorkFolder = dialog.FolderName;
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
