using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Unlike StartupConfigurationManagerDialog, this one has no OK: Save writes the .pubxml files
/// immediately and Publish runs in place, so there is no pending result for a caller to commit. What
/// the caller reads afterwards is only what the status-bar picker needs to refresh from.
/// </summary>
public partial class PublishDialog : Window
{
    public PublishDialog(PublishDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private PublishDialogViewModel ViewModel => (PublishDialogViewModel)DataContext;

    /// <summary>The profiles as they now stand on disk, valid once the dialog has closed.</summary>
    public IReadOnlyList<string> ResultProfileNames => ViewModel.ResultProfileNames;

    public string? ResultSelectedProfileName => ViewModel.ResultSelectedProfileName;

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Covers the Close button, Esc and the title-bar X alike. A publish in flight blocks the close
    /// outright rather than being abandoned: the dotnet process would keep running with nothing left
    /// showing its output.
    /// </summary>
    private void PublishDialog_OnClosing(object sender, CancelEventArgs e)
    {
        if (ViewModel.IsPublishing)
        {
            MessageBox.Show(
                this,
                "A publish is still running. Wait for it to finish before closing.",
                "IDE++",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        if (!ViewModel.HasUnsavedChanges)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Discard unsaved changes to the publish profiles?",
            "IDE++",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        e.Cancel = result != MessageBoxResult.Yes;
    }

    /// <summary>PublishDir is conventionally written relative to the project, so a folder picked
    /// inside the project tree is stored that way; anything outside it stays absolute.</summary>
    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProfile is not { } profile)
        {
            return;
        }

        var projectDirectory = ViewModel.ProjectDirectory;
        var dialog = new OpenFolderDialog
        {
            Title = "Publish output folder",
            InitialDirectory = Directory.Exists(projectDirectory) ? projectDirectory : null,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var relative = Path.GetRelativePath(projectDirectory, dialog.FolderName);
        profile.PublishDir = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? dialog.FolderName
            : relative;
    }
}
