using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace IdePlusPlus.Editor;

public sealed class CurrentLineBackgroundRenderer : IBackgroundRenderer
{
    public int? CurrentLine { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (CurrentLine is not { } line || line < 1 || line > textView.Document.LineCount)
        {
            return;
        }

        var documentLine = textView.Document.GetLineByNumber(line);
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
        builder.AddSegment(textView, documentLine);

        var geometry = builder.CreateGeometry();
        if (geometry is not null)
        {
            drawingContext.DrawGeometry(EditorTheme.CurrentLineHighlight, null, geometry);
        }
    }
}
