using System.IO;
using System.Windows;
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
    private readonly CaretLineBackgroundRenderer _caretLineRenderer = new();
    private readonly DiagnosticSquiggleRenderer _diagnosticSquiggleRenderer = new();
    private readonly GhostTextGenerator _ghostTextGenerator = new();
    private readonly CtrlHoverUnderlineColorizer _ctrlHoverColorizer = new();
    private readonly DispatcherTimer _completionDebounceTimer;
    private FoldingManager? _foldingManager;
    private DocumentViewModel? _boundDocument;
    private DiffPreview? _currentPreview;
    private CancellationTokenSource? _completionCancellation;
    private CancellationTokenSource? _memberCompletionCancellation;
    private CancellationTokenSource? _hoverCancellation;
    private CancellationTokenSource? _definitionCancellation;
    private ToolTip? _hoverToolTip;
    private CompletionWindow? _activeCompletionWindow;

    public TextEditorHost()
    {
        InitializeComponent();
        SearchPanel.Install(Editor);

        Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_caretLineRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_currentLineRenderer);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticSquiggleRenderer);
        Editor.TextArea.TextView.ElementGenerators.Add(_ghostTextGenerator);
        Editor.TextArea.TextView.LineTransformers.Add(_ctrlHoverColorizer);
        EditorServices.DebugLocation.Changed += OnDebugLocationChanged;
        EditorServices.GoToLine.Requested += OnGoToLineRequested;
        EditorServices.DiffPreview.Requested += OnDiffPreviewRequested;

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
        Editor.TextArea.PreviewKeyUp += OnEditorPreviewKeyUp;
        Editor.TextArea.TextEntered += OnEditorTextEntered;
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Editor.GotKeyboardFocus += (_, _) => PublishCaretPosition();

        // Neither of these fires unless a plugin has actually registered an editor context-menu
        // command or a hover provider (EditorServices.EditorContextMenuCommands/HoverProviderLookup
        // are null until then) - a document with no such plugin installed behaves exactly as before.
        Editor.ContextMenuOpening += OnEditorContextMenuOpening;
        Editor.TextArea.TextView.MouseHover += OnTextViewMouseHover;
        Editor.TextArea.TextView.MouseHoverStopped += OnTextViewMouseHoverStopped;
        Editor.TextArea.PreviewMouseLeftButtonDown += OnEditorPreviewMouseLeftButtonDown;

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
            EditorServices.DiffPreview.Requested -= OnDiffPreviewRequested;
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
        Editor.TextArea.TextView.InvalidateLayer(_caretLineRenderer.Layer);

        // EditorTheme.Apply also just retinted every built-in syntax-highlighting color in place
        // (see SyntaxHighlightingRecolorer) - a full redraw is needed for already-open documents to
        // pick that up, since InvalidateLayer above only covers the two background renderers.
        Editor.TextArea.TextView.Redraw();
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

        UpdateCaretLineHighlight();

        if (Editor.TextArea.IsKeyboardFocusWithin)
        {
            PublishCaretPosition();
        }
    }

    private void UpdateCaretLineHighlight()
    {
        var line = Editor.TextArea.Caret.Line;
        if (_caretLineRenderer.Line == line)
        {
            return;
        }

        _caretLineRenderer.Line = line;
        Editor.TextArea.TextView.InvalidateLayer(_caretLineRenderer.Layer);
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

        // A pending/showing hover belongs to whatever document was bound when the mouse stopped
        // moving - switching documents (e.g. via a tab click) shouldn't leave it dangling or let a
        // slow in-flight lookup apply its result to the newly-bound document.
        _hoverCancellation?.Cancel();
        CloseHoverToolTip();

        // Same reasoning as _hoverCancellation just above - a go-to-definition lookup in flight
        // belongs to whatever document was focused when Ctrl+Click fired.
        _definitionCancellation?.Cancel();
        ClearCtrlHoverAffordance();

        // The squiggle renderer is shared by every document this editor ever binds to - clear
        // whatever the previous document's diagnostics were so they don't briefly show over the
        // newly-bound document until its own (possibly empty) diagnostics arrive.
        _diagnosticSquiggleRenderer.SetDiagnostics([]);
        Editor.TextArea.TextView.InvalidateLayer(_diagnosticSquiggleRenderer.Layer);

        // The diff panel belongs to whichever document was previously bound - clear it before
        // rebinding, then below pull whatever preview is current for the newly-bound document (if
        // any), rather than waiting on the live Requested event, which fires nothing if it fired
        // before this host was even constructed - see DiffPreviewRequest's remarks.
        ClearDiffPreview();

        _boundDocument = DataContext as DocumentViewModel;
        _breakpointMargin.FilePath = _boundDocument?.FilePath;

        if (_boundDocument is null)
        {
            Editor.Document = null;
            _caretLineRenderer.Line = null;
            return;
        }

        Editor.Document = _boundDocument.TextDocument;
        Editor.SyntaxHighlighting = _boundDocument.SyntaxHighlighting;

        if (EditorServices.DiffPreview.CurrentFor(_boundDocument) is { } pendingPreview)
        {
            ShowDiffPreview(pendingPreview);
        }

        // Covers a jump requested (e.g. by Ctrl+Click go-to-definition in another file) before this
        // host existed to hear the live event - see GoToLineRequest's remarks for why.
        if (EditorServices.GoToLine.Consume(_boundDocument) is { } pendingGoTo)
        {
            ApplyGoTo(pendingGoTo.Line, pendingGoTo.Column);
        }

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
        UpdateCaretLineHighlight();
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

    private void OnGoToLineRequested(DocumentViewModel document, int line, int? column)
    {
        if (!ReferenceEquals(_boundDocument, document))
        {
            return;
        }

        // Consume before acting: an already-bound document takes this path (not Rebind's catch-up
        // pull above), so the pending entry GoToLineRequest kept for it must still be cleared.
        EditorServices.GoToLine.Consume(document);
        ApplyGoTo(line, column);
    }

    /// <summary>Moves the caret to line/column (1-based, matching AvalonEdit's own TextEditor.Line/
    /// Column) and scrolls it into view. Shared by the live GoToLineRequested event and Rebind's
    /// catch-up consumption of a jump requested before this host existed.</summary>
    private void ApplyGoTo(int line, int? column)
    {
        if (Editor.Document is null)
        {
            return;
        }

        var clampedLine = Math.Clamp(line, 1, Editor.Document.LineCount);
        var documentLine = Editor.Document.GetLineByNumber(clampedLine);
        var columnOffset = column is { } col ? Math.Clamp(col - 1, 0, documentLine.Length) : 0;
        Editor.CaretOffset = documentLine.Offset + columnOffset;
        Editor.ScrollToLine(clampedLine);
        Editor.Focus();
    }

    private void OnDiffPreviewRequested(DocumentViewModel document, DiffPreview? preview)
    {
        if (!ReferenceEquals(_boundDocument, document))
        {
            return;
        }

        if (preview is null)
        {
            ClearDiffPreview();
        }
        else
        {
            ShowDiffPreview(preview);
        }
    }

    private void ShowDiffPreview(DiffPreview preview)
    {
        _currentPreview = preview;
        ChangeSummaryText.Text = preview.ChangeSummary;
        DiffLinesControl.ItemsSource = preview.Lines;
        DecisionButtonsPanel.Visibility = preview.IsDecided ? Visibility.Collapsed : Visibility.Visible;
        DecisionTextBlock.Text = preview.DecisionText;
        DecisionTextBlock.Visibility = preview.IsDecided ? Visibility.Visible : Visibility.Collapsed;
        DiffReviewPanel.Visibility = Visibility.Visible;
    }

    private void ClearDiffPreview()
    {
        _currentPreview = null;
        DiffReviewPanel.Visibility = Visibility.Collapsed;
        DiffLinesControl.ItemsSource = null;
    }

    private void AcceptPreviewButton_OnClick(object sender, RoutedEventArgs e) => _currentPreview?.Accept?.Invoke();

    private void RejectPreviewButton_OnClick(object sender, RoutedEventArgs e) => _currentPreview?.Reject?.Invoke();

    private void DismissPreviewButton_OnClick(object sender, RoutedEventArgs e) => _currentPreview?.Dismiss.Invoke();

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

        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            // The mouse can already be sitting still over something when Ctrl goes down - MouseHover
            // only re-fires on movement, so re-evaluate the affordance at the current position too.
            if (GetOffsetAt(Mouse.GetPosition(Editor.TextArea.TextView)) is { } offset)
            {
                _ = ShowDefinitionAffordanceAsync(offset);
            }

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

    // Built in code-behind (not XAML) for the same reason Solution Explorer's project context menu
    // is - a ContextMenu's popup isn't part of the visual tree its PlacementTarget lives in, and its
    // contents (which plugin commands apply right now) can only be known at the moment it opens
    // anyway. A document with no applicable plugin command shows no menu at all, same as before any
    // plugin ever registered one.
    private void OnEditorContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        Editor.ContextMenu = null;

        if (EditorServices.EditorContextMenuCommands?.Invoke() is not { Count: > 0 } commands
            || _boundDocument?.FilePath is not { } filePath)
        {
            e.Handled = true;
            return;
        }

        var state = new EditorContextMenuState(
            filePath,
            string.IsNullOrEmpty(Editor.SelectedText) ? null : Editor.SelectedText,
            Editor.TextArea.Caret.Line,
            Editor.TextArea.Caret.Column);

        var applicable = commands.Where(c => c.CanExecute(state)).ToList();
        if (applicable.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        foreach (var command in applicable)
        {
            var menuItem = new MenuItem { Header = command.Header };
            menuItem.Click += (_, _) => command.Execute(state);
            menu.Items.Add(menuItem);
        }

        Editor.ContextMenu = menu;
    }

    /// <summary>Fires after the mouse comes to rest over a point in the text view - looks up the
    /// word under the cursor and, if a plugin registered a hover provider for this document's file
    /// extension, asks it for text to show in a plain tooltip. Managed as an explicit ToolTip
    /// instance we open/close ourselves (rather than WPF's own automatic hover-driven ToolTip),
    /// since WPF's built-in tooltip has no notion of "which word" within one large text control.</summary>
    private void OnTextViewMouseHover(object sender, MouseEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            // Ctrl-held hover is go-to-definition's affordance, not the plain tooltip - the two
            // shouldn't compete for the same screen space.
            CloseHoverToolTip();
            if (GetOffsetAt(e.GetPosition(Editor.TextArea.TextView)) is { } offset)
            {
                _ = ShowDefinitionAffordanceAsync(offset);
            }
            else
            {
                ClearCtrlHoverAffordance();
            }

            return;
        }

        ClearCtrlHoverAffordance();
        _ = ShowHoverAsync(e);
    }

    private void OnTextViewMouseHoverStopped(object sender, MouseEventArgs e)
    {
        _hoverCancellation?.Cancel();
        CloseHoverToolTip();
        ClearCtrlHoverAffordance();
        e.Handled = true;
    }

    private void CloseHoverToolTip()
    {
        if (_hoverToolTip is { } toolTip)
        {
            toolTip.IsOpen = false;
            _hoverToolTip = null;
        }
    }

    private async Task ShowHoverAsync(MouseEventArgs e)
    {
        if (EditorServices.HoverProviderLookup is not { } lookup
            || _boundDocument?.FilePath is not { } filePath
            || Editor.Document is null)
        {
            return;
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension) || lookup(extension) is not { } getHoverText)
        {
            return;
        }

        var textView = Editor.TextArea.TextView;
        var position = textView.GetPositionFloor(e.GetPosition(textView) + textView.ScrollOffset);
        if (position is not { } textViewPosition)
        {
            return;
        }

        var offset = Editor.Document.GetOffset(textViewPosition.Location);
        if (GetIdentifierSpanAt(Editor.Document, offset) is not { } wordSpan)
        {
            return;
        }

        var word = Editor.Document.GetText(wordSpan.Start, wordSpan.End - wordSpan.Start);

        _hoverCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _hoverCancellation = cancellation;

        var request = new EditorHoverRequest(filePath, textViewPosition.Line, textViewPosition.Column, word);
        string? text;
        try
        {
            text = await getHoverText(request, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellation.IsCancellationRequested || string.IsNullOrEmpty(text))
        {
            return;
        }

        CloseHoverToolTip();
        _hoverToolTip = new ToolTip
        {
            Content = text,
            PlacementTarget = Editor,
            IsOpen = true,
        };
    }

    /// <summary>The span of the identifier at offset (touching it from either side counts), or null
    /// if there isn't one. Shared by the plain-hover pipeline above, the Ctrl-hover go-to-definition
    /// affordance, and the Ctrl+Click handler below - one word-boundary rule everywhere.</summary>
    private static (int Start, int End)? GetIdentifierSpanAt(TextDocument document, int offset)
    {
        if (offset < 0 || offset > document.TextLength)
        {
            return null;
        }

        var start = offset;
        var end = offset;

        while (start > 0 && IsWordChar(document.GetCharAt(start - 1)))
        {
            start--;
        }

        while (end < document.TextLength && IsWordChar(document.GetCharAt(end)))
        {
            end++;
        }

        return end > start ? (start, end) : null;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Document offset under a TextView-relative point, or null if the point isn't over any
    /// character at all. Shared by the Ctrl-hover affordance (which has a real mouse position) and
    /// Ctrl-key-down (which doesn't, and reads the OS's current pointer position instead).</summary>
    private int? GetOffsetAt(Point textViewPoint)
    {
        var textView = Editor.TextArea.TextView;
        if (Editor.Document is null || textView.GetPositionFloor(textViewPoint + textView.ScrollOffset) is not { } position)
        {
            return null;
        }

        return Editor.Document.GetOffset(position.Location);
    }

    private void OnEditorPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            ClearCtrlHoverAffordance();
        }
    }

    /// <summary>
    /// Underlines the identifier at offset and switches to a hand cursor, but only when Ctrl+Click
    /// there would actually jump somewhere (<see cref="EditorDefinitionOutcome.Found"/>) - reusing
    /// the exact resolver the click itself uses, so the affordance can never promise a jump the
    /// click can't deliver (a BCL/NuGet member or an unresolved word gets no underline).
    /// </summary>
    private async Task ShowDefinitionAffordanceAsync(int offset)
    {
        if (Editor.Document is null || _boundDocument?.FilePath is not { } filePath
            || EditorServices.DefinitionProvider is not { } provider
            || GetIdentifierSpanAt(Editor.Document, offset) is not { } span)
        {
            ClearCtrlHoverAffordance();
            return;
        }

        _definitionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _definitionCancellation = cancellation;
        var text = Editor.Document.Text;

        EditorDefinitionOutcome? outcome;
        try
        {
            outcome = await provider.GetDefinitionAsync(filePath, text, offset, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellation.IsCancellationRequested || Editor.Document is null)
        {
            return;
        }

        if (outcome is not EditorDefinitionOutcome.Found)
        {
            ClearCtrlHoverAffordance();
            return;
        }

        _ctrlHoverColorizer.Span = span;
        Editor.TextArea.Cursor = Cursors.Hand;
        Editor.TextArea.TextView.Redraw();
    }

    private void ClearCtrlHoverAffordance()
    {
        if (_ctrlHoverColorizer.Span is null && Editor.TextArea.Cursor is null)
        {
            return;
        }

        _ctrlHoverColorizer.Span = null;
        Editor.TextArea.Cursor = null;
        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>
    /// Ctrl+Click go-to-definition. Reuses the same pixel->offset resolution the hover pipeline
    /// uses; only claims the click (<c>e.Handled = true</c>) once an identifier is actually found
    /// under the pointer, so a plain Ctrl+click on whitespace still places the caret normally - the
    /// first place this codebase suppresses a mouse (not keyboard/wheel) default via Preview.
    /// </summary>
    private void OnEditorPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || Editor.Document is null || _boundDocument?.FilePath is not { } filePath)
        {
            return;
        }

        if (GetOffsetAt(e.GetPosition(Editor.TextArea.TextView)) is not { } offset
            || GetIdentifierSpanAt(Editor.Document, offset) is not { } span)
        {
            return;
        }

        e.Handled = true;
        var word = Editor.Document.GetText(span.Start, span.End - span.Start);
        _ = NavigateToDefinitionAsync(filePath, offset, word);
    }

    private async Task NavigateToDefinitionAsync(string filePath, int offset, string word)
    {
        if (EditorServices.DefinitionProvider is not { } provider || Editor.Document is null)
        {
            return;
        }

        ClearCtrlHoverAffordance();
        var targetDocument = _boundDocument;
        var text = Editor.Document.Text;

        _definitionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _definitionCancellation = cancellation;

        EditorDefinitionOutcome? outcome;
        try
        {
            outcome = await provider.GetDefinitionAsync(filePath, text, offset, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Stale: a newer Ctrl+Click superseded this one, or the bound document changed underneath
        // it (a tab switch) while the lookup was in flight - same staleness guard RequestCompletionAsync
        // already uses.
        if (cancellation.IsCancellationRequested || !ReferenceEquals(_boundDocument, targetDocument))
        {
            return;
        }

        switch (outcome)
        {
            case EditorDefinitionOutcome.Found found when string.Equals(found.FilePath, filePath, StringComparison.OrdinalIgnoreCase):
                ApplyGoTo(found.Line, found.Column);
                return;

            case EditorDefinitionOutcome.Found found:
                EditorServices.OpenAndGoTo?.Invoke(found.FilePath, found.Line, found.Column);
                return;

            case EditorDefinitionOutcome.NoSourceAvailable noSource:
                EditorServices.StatusLog?.Invoke($"'{noSource.SymbolDisplayName}' has no source available (defined outside this solution).");
                return;

            default:
                EditorServices.StatusLog?.Invoke($"No definition found for '{word}'.");
                return;
        }
    }
}
