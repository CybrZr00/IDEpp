using IdePlusPlus.Editor.Diff;

namespace IdePlusPlus.Editor;

/// <summary>One proposed edit's review state, as shown docked above the code in the editor tab for
/// the document it targets. Accept/Reject are null once IsDecided - the panel shows DecisionText and
/// no buttons instead. Dismiss is always available, even once decided, so an already-applied/rejected
/// panel doesn't have to sit there until the user navigates away.</summary>
public sealed record DiffPreview(
    IReadOnlyList<DiffLine> Lines,
    string ChangeSummary,
    bool IsLargeDeletion,
    bool IsDecided,
    string? DecisionText,
    Action? Accept,
    Action? Reject,
    Action Dismiss);

/// <summary>
/// A simple pub/sub bridge so a ViewModel (which has no reference to the focused TextEditorHost
/// control) can ask the editor bound to a specific document to show a proposed-edit diff - the same
/// ambient-event pattern GoToLineRequest already uses. Unlike GoToLineRequest, this also remembers the
/// last-pushed preview per document: a TextEditorHost for a document that was never open before this
/// session is only constructed by WPF on the next layout pass after the document becomes active, so a
/// preview requested in the same call frame as opening the file would otherwise arrive before any host
/// exists to hear it. TextEditorHost.Rebind() pulls CurrentFor its newly-bound document to cover that.
/// </summary>
public sealed class DiffPreviewRequest
{
    private readonly Dictionary<DocumentViewModel, DiffPreview> _current = new();

    public event Action<DocumentViewModel, DiffPreview?>? Requested;

    public void Request(DocumentViewModel document, DiffPreview? preview)
    {
        if (preview is null)
        {
            _current.Remove(document);
        }
        else
        {
            _current[document] = preview;
        }

        Requested?.Invoke(document, preview);
    }

    public DiffPreview? CurrentFor(DocumentViewModel document) => _current.GetValueOrDefault(document);
}
