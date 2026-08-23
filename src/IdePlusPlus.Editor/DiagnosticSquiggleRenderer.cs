using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace IdePlusPlus.Editor;

/// <summary>
/// Draws a colored wavy underline under each live diagnostic's span, keyed off the document
/// currently bound to the editor. Mirrors CurrentLineBackgroundRenderer's IBackgroundRenderer shape.
/// TextEditorHost owns the one instance per editor and calls SetDiagnostics whenever
/// ILiveDiagnosticsProvider.DiagnosticsChanged fires for the bound file, then invalidates this
/// renderer's layer to force a repaint.
/// </summary>
public sealed class DiagnosticSquiggleRenderer : IBackgroundRenderer
{
    private IReadOnlyList<LiveDiagnostic> _diagnostics = [];

    // KnownLayer.Selection is what CurrentLineBackgroundRenderer already uses for a full-line
    // highlight; drawing the squiggle in the same layer but registering it after (see
    // TextEditorHost's BackgroundRenderers.Add order) keeps it visually on top of that highlight
    // without reaching into the text-rendering layer itself.
    public KnownLayer Layer => KnownLayer.Selection;

    public void SetDiagnostics(IReadOnlyList<LiveDiagnostic> diagnostics) => _diagnostics = diagnostics;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_diagnostics.Count == 0 || textView.Document is null)
        {
            return;
        }

        var lineCount = textView.Document.LineCount;

        foreach (var diagnostic in _diagnostics)
        {
            if (diagnostic.Line < 1 || diagnostic.Line > lineCount)
            {
                continue;
            }

            var brush = diagnostic.Severity == "Error"
                ? EditorTheme.DiagnosticErrorSquiggle
                : EditorTheme.DiagnosticWarningSquiggle;

            var endLineNumber = Math.Min(Math.Max(diagnostic.EndLine, diagnostic.Line), lineCount);

            // Draw one wavy underline per document line the span touches, rather than a single
            // underline across the combined bounding box - a multi-line diagnostic would otherwise
            // draw one huge, mostly-empty wave spanning every line's full height in between.
            for (var lineNumber = diagnostic.Line; lineNumber <= endLineNumber; lineNumber++)
            {
                var documentLine = textView.Document.GetLineByNumber(lineNumber);
                var startColumn = lineNumber == diagnostic.Line ? diagnostic.Column : 1;
                var endColumn = lineNumber == endLineNumber ? diagnostic.EndColumn : documentLine.Length + 1;

                startColumn = Math.Clamp(startColumn, 1, documentLine.Length + 1);
                endColumn = Math.Clamp(endColumn, 1, documentLine.Length + 1);

                var startOffset = documentLine.Offset + startColumn - 1;
                var endOffset = documentLine.Offset + endColumn - 1;
                if (endOffset <= startOffset)
                {
                    endOffset = Math.Min(startOffset + 1, documentLine.EndOffset);
                }

                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, ExtendToFullWidthAtLineEnd = false };
                builder.AddSegment(textView, new TextSegment { StartOffset = startOffset, Length = endOffset - startOffset });

                var geometry = builder.CreateGeometry();
                if (geometry is null)
                {
                    continue;
                }

                DrawWavyUnderline(drawingContext, geometry.Bounds, brush);
            }
        }
    }

    private static void DrawWavyUnderline(DrawingContext drawingContext, Rect bounds, Brush brush)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        const double amplitude = 1.5;
        const double wavelength = 4.0;

        var y = bounds.Bottom - 1.0;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(bounds.Left, y), false, false);
            var x = bounds.Left;
            var up = true;
            while (x < bounds.Right)
            {
                var nextX = Math.Min(x + wavelength / 2, bounds.Right);
                context.LineTo(new Point(nextX, y + (up ? -amplitude : amplitude)), true, false);
                x = nextX;
                up = !up;
            }
        }

        geometry.Freeze();
        var pen = new Pen(brush, 1.0);
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}
