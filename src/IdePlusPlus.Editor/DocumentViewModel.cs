using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

namespace IdePlusPlus.Editor;

public sealed partial class DocumentViewModel : ObservableObject
{
    private DocumentViewModel(string contentId, string? initialFilePath, TextDocument textDocument)
    {
        ContentId = contentId;
        TextDocument = textDocument;
        filePath = initialFilePath;
        SyntaxHighlighting = HighlightingRegistry.GetHighlightingFor(initialFilePath);

        // Wired after the initial content/path above, so loading a file's text into
        // TextDocument doesn't itself mark the freshly-opened document as dirty.
        TextDocument.TextChanged += (_, _) => IsDirty = true;
    }

    /// <summary>Stable identity for the AvalonDock layout — the file path once saved, otherwise the untitled id.</summary>
    public string ContentId { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool isDirty;

    public TextDocument TextDocument { get; }

    public IHighlightingDefinition? SyntaxHighlighting { get; private set; }

    public string Title
    {
        get
        {
            var name = FilePath is null ? "Untitled" : Path.GetFileName(FilePath);
            return IsDirty ? name + "*" : name;
        }
    }

    public static DocumentViewModel ForNewFile(string untitledName) =>
        new($"untitled://{untitledName}", null, new TextDocument());

    public static DocumentViewModel ForExistingFile(string filePath) =>
        new(filePath, filePath, new TextDocument(File.ReadAllText(filePath)));

    public void AttachToFile(string newFilePath)
    {
        FilePath = newFilePath;
        ContentId = newFilePath;
        SyntaxHighlighting = HighlightingRegistry.GetHighlightingFor(newFilePath);
        OnPropertyChanged(nameof(SyntaxHighlighting));
    }
}
