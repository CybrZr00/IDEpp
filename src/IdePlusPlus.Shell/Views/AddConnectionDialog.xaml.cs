using System.Windows;

namespace IdePlusPlus.Shell.Views;

public partial class AddConnectionDialog : Window
{
    public AddConnectionDialog(AddConnectionDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private AddConnectionDialogViewModel ViewModel => (AddConnectionDialogViewModel)DataContext;

    private void AddButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.Name) && !string.IsNullOrWhiteSpace(ViewModel.ConnectionString))
        {
            DialogResult = true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
