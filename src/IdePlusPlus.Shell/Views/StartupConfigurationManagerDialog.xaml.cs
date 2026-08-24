using System.Windows;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell.Views;

public partial class StartupConfigurationManagerDialog : Window
{
    public StartupConfigurationManagerDialog(StartupConfigurationManagerViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private StartupConfigurationManagerViewModel ViewModel => (StartupConfigurationManagerViewModel)DataContext;

    /// <summary>Valid once the dialog has closed with DialogResult == true.</summary>
    public IReadOnlyList<StartupConfiguration> ResultConfigurations => ViewModel.BuildResultConfigurations();

    /// <summary>Valid once the dialog has closed with DialogResult == true.</summary>
    public string ResultActiveConfigurationName => ViewModel.ActiveConfigurationName;

    private void OkButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
