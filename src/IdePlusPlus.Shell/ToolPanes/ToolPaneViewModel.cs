using CommunityToolkit.Mvvm.ComponentModel;

namespace IdePlusPlus.Shell.ToolPanes;

public abstract class ToolPaneViewModel : ObservableObject
{
    protected ToolPaneViewModel(string contentId, string title)
    {
        ContentId = contentId;
        Title = title;
    }

    public string ContentId { get; }

    public string Title { get; }
}
