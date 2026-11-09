using IdePlusPlus.Editor;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// A binary opened as its own editor tab. It subclasses <see cref="DocumentViewModel"/> so it fits the
/// existing document collection, docking and close handling unchanged - but it is never dirty and must
/// never be saved (its backing text is empty; writing it would overwrite the binary). The actual UI is
/// the shared <see cref="BinaryViewerViewModel"/>, which starts loading as soon as the tab opens.
/// </summary>
public sealed class BinaryDocumentViewModel : DocumentViewModel
{
    public BinaryDocumentViewModel(string filePath)
        : base(filePath, filePath)
    {
        Viewer = new BinaryViewerViewModel { BinaryPath = filePath };
        _ = Viewer.LoadCommand.ExecuteAsync(null);
    }

    public BinaryViewerViewModel Viewer { get; }
}
