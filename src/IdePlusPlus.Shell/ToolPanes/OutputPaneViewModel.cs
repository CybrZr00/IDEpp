using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class OutputPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "Output";

    private readonly StringBuilder _buffer = new();

    public OutputPaneViewModel()
        : base(PaneContentId, "Output")
    {
    }

    [ObservableProperty]
    private string text = "Build and run output will stream here.";

    public void AppendLine(string line)
    {
        _buffer.AppendLine(line);
        Text = _buffer.ToString();
    }

    public void Clear()
    {
        _buffer.Clear();
        Text = string.Empty;
    }
}
