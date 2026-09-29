using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

namespace AccessVbaPlugin;

/// <summary>
/// Renders an inline FIM suggestion as dim italic text at a document offset without inserting it.
/// A copy of IDE++'s own GhostTextGenerator: that one lives in IdePlusPlus.Editor, which plugins
/// deliberately can't reference, while AvalonEdit itself is shared across the plugin boundary.
/// </summary>
internal sealed class VbaGhostTextGenerator : VisualLineElementGenerator
{
    private static readonly Brush Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    public string? SuggestionText { get; set; }

    public int SuggestionOffset { get; set; } = -1;

    public bool HasSuggestion => !string.IsNullOrEmpty(SuggestionText) && SuggestionOffset >= 0;

    public void Clear()
    {
        SuggestionText = null;
        SuggestionOffset = -1;
    }

    public override int GetFirstInterestedOffset(int startOffset)
        => HasSuggestion && SuggestionOffset >= startOffset ? SuggestionOffset : -1;

    public override VisualLineElement? ConstructElement(int offset)
        => HasSuggestion && offset == SuggestionOffset ? new GhostTextVisualElement(SuggestionText!) : null;

    /// <summary>
    /// TextRunProperties is null until AvalonEdit's VisualLine assigns it, which happens after
    /// ConstructElement returns but before CreateTextRun is called - so the styling has to be
    /// applied here, not in the generator.
    /// </summary>
    private sealed class GhostTextVisualElement(string text) : FormattedTextElement(text, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            if (TextRunProperties is not null)
            {
                TextRunProperties.SetForegroundBrush(Foreground);
                var baseTypeface = TextRunProperties.Typeface;
                TextRunProperties.SetTypeface(new Typeface(
                    baseTypeface.FontFamily, FontStyles.Italic, baseTypeface.Weight, baseTypeface.Stretch));
            }

            return base.CreateTextRun(startVisualColumn, context);
        }
    }
}
