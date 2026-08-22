using System.Windows;
using System.Windows.Input;

namespace IdePlusPlus.Shell.Views;

public partial class GoToLineWindow : Window
{
    public int? LineNumber { get; private set; }

    public GoToLineWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LineNumberBox.Focus();
    }

    private void OkButton_OnClick(object sender, RoutedEventArgs e) => Accept();

    private void LineNumberBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
        }
    }

    private void Accept()
    {
        if (int.TryParse(LineNumberBox.Text, out var line) && line > 0)
        {
            LineNumber = line;
            DialogResult = true;
        }
    }
}
