using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

namespace IdePlusPlus.Editor;

/// <summary>Renders an inline FIM suggestion (dim italic text) at a document offset without inserting it.</summary>
public sealed class GhostTextGenerator : VisualLineElementGenerator
{
    public string? SuggestionText { get; set; }

    public int SuggestionOffset { get; set; } = -1;

    public bool HasSuggestion => !string.IsNullOrEmpty(SuggestionText) && SuggestionOffset >= 0;

    public void Clear()
    {
        SuggestionText = null;
        SuggestionOffset = -1;
    }

    public override int GetFirstInterestedOffset(int startOffset) =>
        HasSuggestion && SuggestionOffset >= startOffset ? SuggestionOffset : -1;

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (!HasSuggestion || offset != SuggestionOffset)
        {
            return null;
        }

        return new GhostTextVisualElement(SuggestionText!);
    }

    /// <summary>
    /// TextRunProperties is null until AvalonEdit's VisualLine assigns it, which happens after
    /// ConstructElement returns but before CreateTextRun is called - so the dim italic styling
    /// has to be applied here, not in the generator.
    /// </summary>
    private sealed class GhostTextVisualElement(string text) : FormattedTextElement(text, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            if (TextRunProperties is not null)
            {
                TextRunProperties.SetForegroundBrush(EditorTheme.GhostTextForeground);
                var baseTypeface = TextRunProperties.Typeface;
                TextRunProperties.SetTypeface(new Typeface(
                    baseTypeface.FontFamily, FontStyles.Italic, baseTypeface.Weight, baseTypeface.Stretch));
            }

            return base.CreateTextRun(startVisualColumn, context);
        }
    }
}
