using System.Collections.ObjectModel;
using IdePlusPlus.Debug;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed class CallStackPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "CallStack";

    public CallStackPaneViewModel()
        : base(PaneContentId, "Call Stack")
    {
    }

    public ObservableCollection<StackFrameInfo> Frames { get; } = [];
}
