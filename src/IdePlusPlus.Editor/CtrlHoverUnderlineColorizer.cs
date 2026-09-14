using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace IdePlusPlus.Editor;

/// <summary>
/// Underlines one identifier span - Ctrl+Click go-to-definition's hover affordance. A
/// <see cref="DocumentColorizingTransformer"/> is AvalonEdit's mechanism for restyling EXISTING
/// document text, distinct from <see cref="GhostTextGenerator"/>'s <see cref="VisualLineElementGenerator"/>,
/// which only inserts new text. TextEditorHost sets <see cref="Span"/> only when
/// EditorServices.DefinitionProvider has actually resolved the hovered word to a real jump target,
/// so the underline can never promise a click that would just show "no definition found".
/// </summary>
public sealed class CtrlHoverUnderlineColorizer : DocumentColorizingTransformer
{
    public (int Start, int End)? Span { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Span is not { } span || span.End <= line.Offset || span.Start >= line.EndOffset)
        {
            return;
        }

        var start = Math.Max(span.Start, line.Offset);
        var end = Math.Min(span.End, line.EndOffset);
        ChangeLinePart(start, end, element => element.TextRunProperties.SetTextDecorations(TextDecorations.Underline));
    }
}
