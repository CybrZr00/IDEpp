using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace IdePlusPlus.Editor;

public sealed class BreakpointMargin : AbstractMargin
{
    private const double Diameter = 10;
    private const double MarginWidth = 18;

    private readonly BreakpointManager _breakpointManager;

    public BreakpointMargin(BreakpointManager breakpointManager)
    {
        _breakpointManager = breakpointManager;
        _breakpointManager.Changed += InvalidateVisual;
    }

    public string? FilePath { get; set; }

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, RenderSize.Width, RenderSize.Height));

        var textView = TextView;
        if (textView is null || FilePath is null || !textView.VisualLinesValid)
        {
            return;
        }

        foreach (var line in _breakpointManager.GetLines(FilePath))
        {
            if (line < 1 || line > textView.Document.LineCount)
            {
                continue;
            }

            var visualTop = textView.GetVisualTopByDocumentLine(line) - textView.VerticalOffset;
            var height = textView.GetVisualLine(line)?.Height ?? textView.DefaultLineHeight;
            var center = new Point(RenderSize.Width / 2, visualTop + (height / 2));
            drawingContext.DrawEllipse(Brushes.Firebrick, null, center, Diameter / 2, Diameter / 2);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (FilePath is null || TextView is null)
        {
            return;
        }

        var visualTop = e.GetPosition(this).Y + TextView.VerticalOffset;
        var line = TextView.GetDocumentLineByVisualTop(visualTop);
        if (line is not null)
        {
            _breakpointManager.Toggle(FilePath, line.LineNumber);
        }
    }
}
