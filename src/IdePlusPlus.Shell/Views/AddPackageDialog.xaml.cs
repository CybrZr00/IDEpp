using System.Windows;
using System.Windows.Input;

namespace IdePlusPlus.Shell.Views;

public partial class AddPackageDialog : Window
{
    public AddPackageDialog(AddPackageDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private AddPackageDialogViewModel ViewModel => (AddPackageDialogViewModel)DataContext;

    private void SearchTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel.SearchCommand.CanExecute(null))
        {
            ViewModel.SearchCommand.Execute(null);
        }
    }

    private void ResultsListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedResult is not null)
        {
            DialogResult = true;
        }
    }

    private void InstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedResult is not null)
        {
            DialogResult = true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
