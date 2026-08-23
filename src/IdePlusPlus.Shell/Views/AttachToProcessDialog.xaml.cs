using System.Windows;
using System.Windows.Input;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell.Views;

public partial class AttachToProcessDialog : Window
{
    public AttachToProcessDialog(AttachToProcessDialogViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private AttachToProcessDialogViewModel ViewModel => (AttachToProcessDialogViewModel)DataContext;

    /// <summary>The process id to attach to, once the dialog has closed with DialogResult == true.</summary>
    public int? SelectedProcessId => ViewModel.SelectedCandidate?.ProcessId;

    private void CandidatesListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedCandidate is not null)
        {
            DialogResult = true;
        }
    }

    private void AttachButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCandidate is not null)
        {
            DialogResult = true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
