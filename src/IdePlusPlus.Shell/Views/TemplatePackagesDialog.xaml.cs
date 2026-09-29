using System.ComponentModel;
using System.Windows;

namespace IdePlusPlus.Shell.Views;

public partial class TemplatePackagesDialog : Window
{
    public TemplatePackagesDialog(TemplatePackagesDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private TemplatePackagesDialogViewModel ViewModel => (TemplatePackagesDialogViewModel)DataContext;

    /// <summary>Whether anything was installed or removed, so the caller can re-read the template
    /// list only when there is a reason to.</summary>
    public bool PackagesChanged => ViewModel.PackagesChanged;

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>An install in flight blocks the close, same as the other template dialogs: the dotnet
    /// process would keep running with nothing left showing its result.</summary>
    private void TemplatePackagesDialog_OnClosing(object sender, CancelEventArgs e)
    {
        if (!ViewModel.IsBusy)
        {
            return;
        }

        MessageBox.Show(
            this,
            "The package operation is still running. Wait for it to finish before closing.",
            "IDE++",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        e.Cancel = true;
    }
}
