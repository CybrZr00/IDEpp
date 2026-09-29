using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Closes itself once something has been created, so the caller's only job afterwards is to open or
/// reload what <see cref="ResultSolutionPath"/> names.
/// </summary>
public partial class NewProjectDialog : Window
{
    public NewProjectDialog(NewProjectDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private NewProjectDialogViewModel ViewModel => (NewProjectDialogViewModel)DataContext;

    /// <summary>The project that was created, or null if the dialog was cancelled.</summary>
    public string? ResultProjectPath => ViewModel.ResultProjectPath;

    /// <summary>The solution to open or reload - the one that was already open, or a newly created
    /// one.</summary>
    public string? ResultSolutionPath => ViewModel.ResultSolutionPath;

    private async void NewProjectDialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();

        // Listing templates can take a moment on first run (the SDK builds its cache), so the dialog
        // is already up by then and this is the first point where a result is meaningful.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>The create is async and the dialog stays up while it streams to the Output pane, so
    /// closing is driven by the result appearing rather than by the button click.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel.Created && !ViewModel.IsBusy)
        {
            DialogResult = true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>A create in flight blocks the close outright, same as PublishDialog: the dotnet
    /// process would keep running with nothing left showing its output.</summary>
    private void NewProjectDialog_OnClosing(object sender, CancelEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            return;
        }

        MessageBox.Show(
            this,
            "The project is still being created. Wait for it to finish before closing.",
            "IDE++",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        e.Cancel = true;
    }

    /// <summary>Reloads the template list afterwards, but only when something actually changed -
    /// re-reading the cache on every Close would be work for nothing.</summary>
    private async void TemplatePackagesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Owner?.DataContext is not MainWindowViewModel mainWindow)
        {
            return;
        }

        if (mainWindow.ShowTemplatePackagesDialog(this))
        {
            await ViewModel.LoadAsync();
        }
    }

    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Location for the new project",
            InitialDirectory = Directory.Exists(ViewModel.Location) ? ViewModel.Location : null,
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.Location = dialog.FolderName;
        }
    }
}
