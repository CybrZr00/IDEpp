using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Search;
using IdePlusPlus.Editor.Folding;

namespace IdePlusPlus.Editor;

public partial class TextEditorHost : UserControl
{
    private readonly BraceFoldingStrategy _braceFoldingStrategy = new();
    private readonly XmlFoldingStrategy _xmlFoldingStrategy = new();
    private FoldingManager? _foldingManager;
    private DocumentViewModel? _boundDocument;

    public TextEditorHost()
    {
        InitializeComponent();
        SearchPanel.Install(Editor);
        DataContextChanged += (_, _) => Rebind();
    }

    private void Rebind()
    {
        if (_boundDocument is not null)
        {
            _boundDocument.TextDocument.TextChanged -= OnTextChanged;
        }

        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }

        _boundDocument = DataContext as DocumentViewModel;
        if (_boundDocument is null)
        {
            Editor.Document = null;
            return;
        }

        Editor.Document = _boundDocument.TextDocument;
        Editor.SyntaxHighlighting = _boundDocument.SyntaxHighlighting;

        if (_boundDocument.SyntaxHighlighting is not null)
        {
            _foldingManager = FoldingManager.Install(Editor.TextArea);
            UpdateFoldings();
            _boundDocument.TextDocument.TextChanged += OnTextChanged;
        }
    }

    private void OnTextChanged(object? sender, EventArgs e) => UpdateFoldings();

    private void UpdateFoldings()
    {
        if (_foldingManager is null || _boundDocument is null)
        {
            return;
        }

        var highlightingName = _boundDocument.SyntaxHighlighting?.Name;
        if (highlightingName == "XML")
        {
            _xmlFoldingStrategy.UpdateFoldings(_foldingManager, _boundDocument.TextDocument);
        }
        else if (highlightingName is "C#" or "Json")
        {
            _braceFoldingStrategy.UpdateFoldings(_foldingManager, _boundDocument.TextDocument);
        }
    }
}
