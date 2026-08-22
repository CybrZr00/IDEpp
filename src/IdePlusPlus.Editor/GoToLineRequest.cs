namespace IdePlusPlus.Editor;

/// <summary>
/// A simple pub/sub bridge so a ViewModel (which has no reference to the focused TextEditorHost
/// control) can ask the editor bound to a specific document to jump to a line - the same
/// ambient-event pattern EditorServices.DebugLocation/CaretPosition already use. Every open
/// TextEditorHost subscribes and ignores requests for documents it isn't currently showing.
/// </summary>
public sealed class GoToLineRequest
{
    public event Action<DocumentViewModel, int>? Requested;

    public void Request(DocumentViewModel document, int line) => Requested?.Invoke(document, line);
}
