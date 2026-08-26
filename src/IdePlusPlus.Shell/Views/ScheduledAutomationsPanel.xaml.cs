using System.Windows;
using System.Windows.Controls;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell.Views;

public partial class ScheduledAutomationsPanel : UserControl
{
    public ScheduledAutomationsPanel() => InitializeComponent();

    private void RunNowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduleRowViewModel row } && DataContext is ScheduledAutomationsPaneViewModel viewModel)
        {
            viewModel.RunNowCommand.Execute(row);
        }
    }

    private void DeleteScheduleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduleRowViewModel row } && DataContext is ScheduledAutomationsPaneViewModel viewModel)
        {
            viewModel.DeleteScheduleCommand.Execute(row);
        }
    }

    private void ScheduleEnabledCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ScheduleRowViewModel row } && DataContext is ScheduledAutomationsPaneViewModel viewModel)
        {
            viewModel.ToggleEnabledCommand.Execute(row);
        }
    }
}
