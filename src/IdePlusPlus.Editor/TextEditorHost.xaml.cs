using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using IdePlusPlus.Editor.Folding;

namespace IdePlusPlus.Editor;

public partial class TextEditorHost : UserControl
{
    private const int MaxPrefixChars = 4000;
    private const int MaxSuffixChars = 1000;

    private readonly BraceFoldingStrategy _braceFoldingStrategy = new();
    private readonly XmlFoldingStrategy _xmlFoldingStrategy = new();
    private readonly BreakpointMargin _breakpointMargin = new(EditorServices.BreakpointManager);
    private readonly CurrentLineBackgroundRenderer _currentLineRenderer = new();
    private readonly DiagnosticSquiggleRenderer _diagnosticSquiggleRenderer = new();
    private readonly GhostTextGenerator _ghostTextGenerator = new();
    private readonly DispatcherTimer _completionDebounceTimer;
    private FoldingManager? _foldingManager;
    private DocumentViewModel? _boundDocument;
    private CancellationTokenSource? _completionCancellation;
    private CancellationTokenSource? _memberCompletionCancellation;
    private CompletionWindow? _activeCompletionWindow;

    public TextEditorHost()
    {
        InitializeComponent();
        SearchPanel.Install(Editor);

        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_currentLineRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticSquiggleRenderer);
        Editor.TextArea.TextView.ElementGenerators.Add(_ghostTextGenerator);
        EditorServices.DebugLocation.Changed += OnDebugLocationChanged;
        EditorServices.GoToLine.Requested += OnGoToLineRequested;

        // DiagnosticsProvider is a single app-wide instance set once at startup (see EditorServices'
        // remarks) - subscribed for this control's whole lifetime rather than per-Rebind, with
        // OnDiagnosticsChanged itself filtering to whichever document is currently bound.
        if (EditorServices.DiagnosticsProvider is { } diagnosticsProvider)
        {
            diagnosticsProvider.DiagnosticsChanged += OnDiagnosticsChanged;
        }

        _completionDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _completionDebounceTimer.Tick += async (_, _) =>
        {
            _completionDebounceTimer.Stop();
            PushDocumentChangedToDiagnostics();

            // A Roslyn member-list popup and the single-line ghost-text suggestion must never both
            // be showing at the same caret position - skip requesting ghost text while the popup is open.
            if (_activeCompletionWindow is not null)
            {
                return;
            }

            await RequestCompletionAsync();
        };

        Editor.TextArea.PreviewKeyDown += OnEditorPreviewKeyDown;
        Editor.TextArea.TextEntered += OnEditorTextEntered;
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Editor.GotKeyboardFocus += (_, _) => PublishCaretPosition();

        ApplyEditorTheme();
        EditorTheme.Changed += ApplyEditorTheme;

        ApplyEditorSettings();
        EditorSettings.Changed += ApplyEditorSettings;
        Editor.PreviewMouseWheel += OnEditorPreviewMouseWheel;

        DataContextChanged += (_, _) => Rebind();
        Unloaded += (_, _) =>
        {
            EditorServices.DebugLocation.Changed -= OnDebugLocationChanged;
            EditorServices.GoToLine.Requested -= OnGoToLineRequested;
            EditorTheme.Changed -= ApplyEditorTheme;
            EditorSettings.Changed -= ApplyEditorSettings;

            if (EditorServices.DiagnosticsProvider is { } diagnosticsProvider)
            {
                diagnosticsProvider.DiagnosticsChanged -= OnDiagnosticsChanged;
            }
        };
    }

    private void ApplyEditorTheme()
    {
        Editor.Background = EditorTheme.Background;
        Editor.Foreground = EditorTheme.Foreground;
        Editor.TextArea.TextView.InvalidateLayer(_currentLineRenderer.Layer);
    }

    private void ApplyEditorSettings()
    {
        Editor.WordWrap = EditorSettings.WordWrap;
        Editor.FontSize = EditorSettings.FontSize;
    }

    private static void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (e.Delta > 0)
        {
            EditorSettings.ZoomIn();
        }
        else
        {
            EditorSettings.ZoomOut();
        }

        e.Handled = true;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        // The caret can move away from a pending suggestion without any text changing (arrow keys,
        // a mouse click) - GhostTextGenerator only ever renders at SuggestionOffset, so leaving it
        // set here would make Tab silently insert the stale suggestion at the wrong location.
        if (_ghostTextGenerator.HasSuggestion && Editor.CaretOffset != _ghostTextGenerator.SuggestionOffset)
        {
            ClearGhostText();
        }

        if (Editor.TextArea.IsKeyboardFocusWithin)
        {
            PublishCaretPosition();
        }
    }

    private void PublishCaretPosition() =>
        EditorServices.CaretPosition.Update(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);

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

        ClearGhostText();
        _completionDebounceTimer.Stop();
        _completionCancellation?.Cancel();
        _memberCompletionCancellation?.Cancel();
        _activeCompletionWindow?.Close();

        // The squiggle renderer is shared by every document this editor ever binds to - clear
        // whatever the previous document's diagnostics were so they don't briefly show over the
        // newly-bound document until its own (possibly empty) diagnostics arrive.
        _diagnosticSquiggleRenderer.SetDiagnostics([]);
        Editor.TextArea.TextView.InvalidateLayer(_diagnosticSquiggleRenderer.Layer);

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
        }

        _boundDocument.TextDocument.TextChanged += OnTextChanged;

        if (_boundDocument.FilePath is { } boundFilePath)
        {
            EditorServices.DiagnosticsProvider?.NotifyDocumentOpened(boundFilePath, _boundDocument.TextDocument.Text);
        }

        UpdateCurrentLineHighlight();
        if (Editor.TextArea.IsKeyboardFocusWithin)
        {
            PublishCaretPosition();
        }
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        UpdateFoldings();

        ClearGhostText();
        _completionDebounceTimer.Stop();
        _completionDebounceTimer.Start();
    }

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
        else if (highlightingName is "C#" or "Json" or "JavaScript" or "C++" or "Java" or "CSS" or "PHP" or "PowerShell")
        {
            _braceFoldingStrategy.UpdateFoldings(_foldingManager, _boundDocument.TextDocument);
        }
    }

    /// <summary>Pushes the current full document text to the live-diagnostics provider, debounced by
    /// the same timer as the ghost-text completion request (both fire ~350ms after the last
    /// keystroke) - called from the timer's Tick handler, never synchronously per keystroke.</summary>
    private void PushDocumentChangedToDiagnostics()
    {
        if (_boundDocument?.FilePath is { } filePath && Editor.Document is not null)
        {
            EditorServices.DiagnosticsProvider?.NotifyDocumentChanged(filePath, Editor.Document.Text);
        }
    }

    /// <summary>Raised on whatever background thread RoslynWorkspaceService finished recomputing
    /// diagnostics on - dispatched to the UI thread before touching the TextView, matching every
    /// other cross-thread event subscription in this codebase (e.g. MainWindowViewModel's).</summary>
    private void OnDiagnosticsChanged(string filePath, IReadOnlyList<LiveDiagnostic> diagnostics) =>
        Dispatcher.Invoke(() =>
        {
            if (_boundDocument?.FilePath is null
                || !string.Equals(_boundDocument.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _diagnosticSquiggleRenderer.SetDiagnostics(diagnostics);
            Editor.TextArea.TextView.InvalidateLayer(_diagnosticSquiggleRenderer.Layer);
        });

    private void OnDebugLocationChanged() => UpdateCurrentLineHighlight();

    private void OnGoToLineRequested(DocumentViewModel document, int line)
    {
        if (!ReferenceEquals(_boundDocument, document) || Editor.Document is null)
        {
            return;
        }

        var clampedLine = Math.Clamp(line, 1, Editor.Document.LineCount);
        var documentLine = Editor.Document.GetLineByNumber(clampedLine);
        Editor.CaretOffset = documentLine.Offset;
        Editor.ScrollToLine(clampedLine);
        Editor.Focus();
    }

    private void UpdateCurrentLineHighlight()
    {
        var debugLocation = EditorServices.DebugLocation;
        var isCurrentFile = _boundDocument?.FilePath is not null
            && string.Equals(_boundDocument.FilePath, debugLocation.FilePath, StringComparison.OrdinalIgnoreCase);

        _currentLineRenderer.CurrentLine = isCurrentFile ? debugLocation.Line : null;
        Editor.TextArea.TextView.InvalidateLayer(_currentLineRenderer.Layer);
    }

    private async Task RequestCompletionAsync()
    {
        var provider = EditorServices.CompletionProvider;
        if (provider is null || _boundDocument is null || Editor.Document is null)
        {
            return;
        }

        var targetDocument = _boundDocument;

        _completionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _completionCancellation = cancellation;

        var caretOffset = Editor.CaretOffset;
        var fullText = Editor.Document.Text;
        var prefix = fullText[..caretOffset];
        var suffix = fullText[caretOffset..];

        if (prefix.Length > MaxPrefixChars)
        {
            prefix = prefix[^MaxPrefixChars..];
        }

        if (suffix.Length > MaxSuffixChars)
        {
            suffix = suffix[..MaxSuffixChars];
        }

        string? completion;
        try
        {
            completion = await provider.GetCompletionAsync(prefix, suffix, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A newer keystroke superseded this request, the caret has since moved, or the user switched
        // to a different document while this request was in flight - stale, discard.
        if (cancellation.IsCancellationRequested
            || string.IsNullOrEmpty(completion)
            || !ReferenceEquals(_boundDocument, targetDocument)
            || Editor.CaretOffset != caretOffset)
        {
            return;
        }

        _ghostTextGenerator.SuggestionText = completion;
        _ghostTextGenerator.SuggestionOffset = caretOffset;
        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>Fires after every character AvalonEdit accepts as text input - used to trigger the
    /// Roslyn member-list popup right after a '.'. Not the only trigger: Ctrl+Space in
    /// OnEditorPreviewKeyDown opens the same popup on demand regardless of the last-typed character.</summary>
    private async void OnEditorTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "." || _boundDocument?.FilePath is not { } filePath || Editor.Document is null)
        {
            return;
        }

        await TriggerMemberCompletionAsync(filePath);
    }

    private async Task TriggerMemberCompletionAsync(string filePath)
    {
        if (EditorServices.MemberCompletionProvider is null)
        {
            return;
        }

        // The two completion UIs must never render at the same time/place - clear any pending
        // ghost-text suggestion (and stop its debounce timer) before requesting the member list.
        ClearGhostText();
        _completionDebounceTimer.Stop();
        _completionCancellation?.Cancel();

        await ShowMemberCompletionWindowAsync(filePath);
    }

    private async Task ShowMemberCompletionWindowAsync(string filePath)
    {
        var provider = EditorServices.MemberCompletionProvider;
        if (provider is null || _boundDocument is null || Editor.Document is null)
        {
            return;
        }

        var targetDocument = _boundDocument;
        var caretOffset = Editor.CaretOffset;
        var text = Editor.Document.Text;

        _memberCompletionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _memberCompletionCancellation = cancellation;

        IReadOnlyList<CompletionItemData> items;
        try
        {
            items = await provider.GetCompletionsAsync(filePath, text, caretOffset, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Stale in the same three ways RequestCompletionAsync already guards against: superseded by
        // a newer request, the caret moved, or the user switched documents while this was in flight.
        if (cancellation.IsCancellationRequested
            || items.Count == 0
            || !ReferenceEquals(_boundDocument, targetDocument)
            || Editor.CaretOffset != caretOffset)
        {
            return;
        }

        var window = new CompletionWindow(Editor.TextArea);
        foreach (var item in items)
        {
            window.CompletionList.CompletionData.Add(new RoslynCompletionData(item));
        }

        _activeCompletionWindow = window;
        window.Closed += (_, _) => _activeCompletionWindow = null;
        window.Show();
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9 && _boundDocument?.FilePath is { } filePath)
        {
            EditorServices.BreakpointManager.Toggle(filePath, Editor.TextArea.Caret.Line);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control
            && _boundDocument?.FilePath is { } memberCompletionFilePath)
        {
            e.Handled = true;
            _ = TriggerMemberCompletionAsync(memberCompletionFilePath);
            return;
        }

        if (!_ghostTextGenerator.HasSuggestion)
        {
            return;
        }

        if (e.Key == Key.Tab)
        {
            AcceptGhostTextSuggestion();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearGhostText();
            e.Handled = true;
        }
    }

    private void AcceptGhostTextSuggestion()
    {
        var text = _ghostTextGenerator.SuggestionText!;
        var offset = _ghostTextGenerator.SuggestionOffset;
        _ghostTextGenerator.Clear();

        Editor.Document.Insert(offset, text);
        Editor.CaretOffset = offset + text.Length;
    }

    private void ClearGhostText()
    {
        if (!_ghostTextGenerator.HasSuggestion)
        {
            return;
        }

        _ghostTextGenerator.Clear();
        Editor.TextArea.TextView.Redraw();
    }
}
