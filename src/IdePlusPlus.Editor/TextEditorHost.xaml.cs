using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using IdePlusPlus.Editor.Folding;

namespace IdePlusPlus.Editor;

public partial class TextEditorHost : UserControl
{
    private readonly BraceFoldingStrategy _braceFoldingStrategy = new();
    private readonly XmlFoldingStrategy _xmlFoldingStrategy = new();
    private readonly BreakpointMargin _breakpointMargin = new(EditorServices.BreakpointManager);
    private readonly CurrentLineBackgroundRenderer _currentLineRenderer = new();
    private FoldingManager? _foldingManager;
    private DocumentViewModel? _boundDocument;

    public TextEditorHost()
    {
        InitializeComponent();
        SearchPanel.Install(Editor);

        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_currentLineRenderer);
        EditorServices.DebugLocation.Changed += OnDebugLocationChanged;

        Editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F9 && _boundDocument?.FilePath is { } filePath)
            {
                EditorServices.BreakpointManager.Toggle(filePath, Editor.TextArea.Caret.Line);
                e.Handled = true;
            }
        };

        DataContextChanged += (_, _) => Rebind();
        Unloaded += (_, _) => EditorServices.DebugLocation.Changed -= OnDebugLocationChanged;
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
        _breakpointMargin.FilePath = _boundDocument?.FilePath;

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

        UpdateCurrentLineHighlight();
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

    private void OnDebugLocationChanged() => UpdateCurrentLineHighlight();

    private void UpdateCurrentLineHighlight()
    {
        var debugLocation = EditorServices.DebugLocation;
        var isCurrentFile = _boundDocument?.FilePath is not null
            && string.Equals(_boundDocument.FilePath, debugLocation.FilePath, StringComparison.OrdinalIgnoreCase);

        _currentLineRenderer.CurrentLine = isCurrentFile ? debugLocation.Line : null;
        Editor.TextArea.TextView.InvalidateLayer(_currentLineRenderer.Layer);
    }
}
