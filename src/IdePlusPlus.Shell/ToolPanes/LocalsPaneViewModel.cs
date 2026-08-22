using System.Collections.ObjectModel;
using IdePlusPlus.Debug;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed class LocalsPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "Locals";

    public LocalsPaneViewModel()
        : base(PaneContentId, "Locals")
    {
    }

    public ObservableCollection<VariableInfo> Variables { get; } = [];
}
