using System.Windows;
using System.Windows.Controls;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.Views;

public partial class SourceControlPanel : UserControl
{
    public SourceControlPanel() => InitializeComponent();

    private void StageOrUnstageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GitFileChange file } || DataContext is not SourceControlPaneViewModel viewModel)
        {
            return;
        }

        if (file.IsStaged)
        {
            viewModel.UnstageCommand.Execute(file);
        }
        else
        {
            viewModel.StageCommand.Execute(file);
        }
    }

    private void CheckoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (BranchComboBox.SelectedItem is not GitBranch branch || DataContext is not SourceControlPaneViewModel viewModel)
        {
            return;
        }

        viewModel.CheckoutBranchCommand.Execute(branch);
    }
}
