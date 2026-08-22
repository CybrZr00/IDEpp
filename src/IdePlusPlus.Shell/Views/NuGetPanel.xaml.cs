using System.Windows;
using System.Windows.Controls;
using IdePlusPlus.NuGet;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell.Views;

public partial class NuGetPanel : UserControl
{
    public NuGetPanel() => InitializeComponent();

    private void RemoveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledPackage package } || DataContext is not NuGetPaneViewModel viewModel)
        {
            return;
        }

        viewModel.RemovePackageCommand.Execute(package);
    }
}
