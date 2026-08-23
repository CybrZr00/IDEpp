using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace IdePlusPlus.Editor;

/// <summary>
/// Tints the line the caret is currently on - unlike CurrentLineBackgroundRenderer (which only lights
/// up during a paused debug session), this is always on while a document has focus, giving a constant
/// "you are here" anchor in a long file. Deliberately drawn on KnownLayer.Background (behind
/// selection/text) with EditorTheme.CaretLineHighlight's low alpha, so it never competes with the
/// debugger's own, much bolder execution-line highlight when both land on the same line.
/// </summary>
public sealed class CaretLineBackgroundRenderer : IBackgroundRenderer
{
    public int? Line { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Line is not { } line || line < 1 || line > textView.Document.LineCount)
        {
            return;
        }

        var documentLine = textView.Document.GetLineByNumber(line);
        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            // Spans the full editor width, not just the text - a full-row band is what makes this
            // read as "the current line" rather than just a highlighted run of characters.
            ExtendToFullWidthAtLineEnd = true,
        };
        builder.AddSegment(textView, documentLine);

        var geometry = builder.CreateGeometry();
        if (geometry is not null)
        {
            drawingContext.DrawGeometry(EditorTheme.CaretLineHighlight, null, geometry);
        }
    }
}
