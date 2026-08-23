using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace IdePlusPlus.Editor;

/// <summary>
/// Wraps one Roslyn-sourced <see cref="CompletionItemData"/> for AvalonEdit's CompletionWindow.
/// Deliberately ignores the CompletionWindow's own <c>completionSegment</c> on Complete - Roslyn
/// already computed the exact replacement span for this specific item (which can differ from the
/// window's default "word at caret" segment, e.g. for a partially-typed-with-different-casing
/// match), so that span (ReplacementStart/ReplacementLength) is what actually gets replaced.
/// </summary>
public sealed class RoslynCompletionData(CompletionItemData item) : ICompletionData
{
    public ImageSource? Image => null;

    public string Text => item.DisplayText;

    public object Content => item.DisplayText;

    public object? Description => item.Description;

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestedEventArgs)
    {
        textArea.Document.Replace(item.ReplacementStart, item.ReplacementLength, item.InsertText);
        textArea.Caret.Offset = item.ReplacementStart + item.InsertText.Length;
    }
}
