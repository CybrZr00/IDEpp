using System.Reflection;
using System.Windows;

namespace IdePlusPlus.Shell.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = $"Version {version?.ToString() ?? "unknown"}";
        RuntimeText.Text = $"Running on .NET {Environment.Version}";
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
