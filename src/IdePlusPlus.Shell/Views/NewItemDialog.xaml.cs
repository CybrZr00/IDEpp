using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// Closes itself once a file exists, so the caller's only job afterwards is to open
/// <see cref="ResultFiles"/> and put them in the tree.
/// </summary>
public partial class NewItemDialog : Window
{
    public NewItemDialog(NewItemDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private NewItemDialogViewModel ViewModel => (NewItemDialogViewModel)DataContext;

    public IReadOnlyList<string> ResultFiles => ViewModel.ResultFiles;

    private async void NewItemDialog_OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Opened from a File > New shortlist entry the template is already chosen, so the name is
        // the only thing left to type - and it's pre-selected, since the default ("Class1") is
        // exactly what gets replaced.
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel.Created && !ViewModel.IsBusy)
        {
            DialogResult = true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void NewItemDialog_OnClosing(object sender, CancelEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            return;
        }

        MessageBox.Show(
            this,
            "The file is still being created. Wait for it to finish before closing.",
            "IDE++",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        e.Cancel = true;
    }

    /// <summary>Starts inside the project, since a file added outside it wouldn't be part of the
    /// project at all - the namespace shown updates to match whatever is picked.</summary>
    private void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Folder for the new item",
            InitialDirectory = Directory.Exists(ViewModel.Directory)
                ? ViewModel.Directory
                : ViewModel.Target.ProjectDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.Directory = dialog.FolderName;
        }
    }
}
