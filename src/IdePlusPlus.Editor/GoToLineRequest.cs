namespace IdePlusPlus.Editor;

/// <summary>
/// A simple pub/sub bridge so a ViewModel (which has no reference to the focused TextEditorHost
/// control) can ask the editor bound to a specific document to jump to a line - the same
/// ambient-event pattern EditorServices.DebugLocation/CaretPosition already use. Every open
/// TextEditorHost subscribes and ignores requests for documents it isn't currently showing.
///
/// Also remembers the last-requested jump per document, the same reasoning as DiffPreviewRequest:
/// a TextEditorHost for a document that was never open before this session is only constructed by
/// WPF on the next layout pass after the document becomes active, so a jump requested in the same
/// call frame as opening the file would otherwise arrive before any host exists to hear it.
/// TextEditorHost.Rebind() calls Consume for its newly-bound document to cover that gap. Unlike
/// DiffPreviewRequest's CurrentFor (a non-removing peek - a diff banner is a standing thing to
/// redisplay on every rebind), Consume removes the entry: a go-to jump is one-shot, and replaying
/// a stale one on a later tab-switch back to this document would be wrong.
/// </summary>
public sealed class GoToLineRequest
{
    private readonly Dictionary<DocumentViewModel, (int Line, int? Column)> _pending = new();

    public event Action<DocumentViewModel, int, int?>? Requested;

    public void Request(DocumentViewModel document, int line, int? column = null)
    {
        _pending[document] = (line, column);
        Requested?.Invoke(document, line, column);
    }

    public (int Line, int? Column)? Consume(DocumentViewModel document) =>
        _pending.Remove(document, out var pending) ? pending : null;
}
