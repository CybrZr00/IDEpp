namespace IdePlusPlus.Editor;

/// <summary>
/// One completion candidate, already resolved to a concrete text edit - deliberately not just a
/// display string, since Roslyn completion items often need more than their DisplayText inserted
/// verbatim (different casing, generic angle brackets, an inserted "()" for a method, an added
/// "using" via an additional text edit that isn't modeled here for MVP, etc). ReplacementStart/
/// ReplacementLength describe the span in the document that InsertText replaces (typically the
/// partially-typed member name already at the caret).
/// </summary>
public sealed record CompletionItemData(
    string DisplayText,
    string InsertText,
    string? Description,
    int ReplacementStart,
    int ReplacementLength);

/// <summary>
/// Editor-side abstraction over a background live-analysis workspace's member-list completion
/// (Roslyn's CompletionService, in practice - see IdePlusPlus.Roslyn.RoslynCompletionService). Kept
/// Roslyn-type-free for the same reason as ILiveDiagnosticsProvider. This is a separate, coexisting
/// completion path from IInlineCompletionProvider's single-line ghost-text suggestions - TextEditorHost
/// is responsible for keeping the two mutually exclusive at a given caret position.
/// </summary>
public interface IMemberCompletionProvider
{
    /// <summary>
    /// Returns member-list completions for the given document text at caretOffset (a 0-based char
    /// offset into text, matching AvalonEdit's TextEditor.CaretOffset). Returns an empty list rather
    /// than throwing when no completions apply (e.g. the workspace hasn't loaded this file, or the
    /// position isn't in a completable context).
    /// </summary>
    Task<IReadOnlyList<CompletionItemData>> GetCompletionsAsync(
        string filePath,
        string text,
        int caretOffset,
        CancellationToken cancellationToken = default);
}
