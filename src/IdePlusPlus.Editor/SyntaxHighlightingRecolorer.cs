using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace IdePlusPlus.Editor;

/// <summary>
/// Retints every color AvalonEdit's built-in highlighting definitions define, in place, so keywords/
/// strings/comments/etc. stay legible against whichever theme's editor background is active - see
/// SyntaxColorAdjuster for why a per-token WCAG contrast check is needed rather than a single "is dark
/// mode" palette swap. HighlightingManager.Instance.GetDefinition(name) hands back one shared instance
/// per name (AvalonEdit's own cache), so mutating its NamedHighlightingColors here reaches every editor
/// currently displaying that language - the same "one static, every consumer redraws" pattern
/// EditorTheme already uses for background/foreground.
/// </summary>
public static class SyntaxHighlightingRecolorer
{
    // Every extension HighlightingRegistry maps to a built-in definition name - kept in sync with
    // HighlightingRegistry.DefinitionNameByExtension's distinct values.
    private static readonly string[] BuiltInDefinitionNames =
    [
        "C#", "XML", "Json", "MarkDown", "JavaScript", "HTML", "CSS", "C++",
        "Java", "PHP", "Python", "PowerShell", "VB", "TSQL", "TeX", "Patch",
    ];

    public static void ApplyTheme(Color editorBackground, bool muted)
    {
        foreach (var name in BuiltInDefinitionNames)
        {
            if (HighlightingManager.Instance.GetDefinition(name) is not { } definition)
            {
                continue;
            }

            foreach (var namedColor in definition.NamedHighlightingColors)
            {
                RecolorInPlace(namedColor, editorBackground, muted);
            }
        }
    }

    private static void RecolorInPlace(HighlightingColor namedColor, Color editorBackground, bool muted)
    {
        // Built-in HighlightingColor instances are plain mutable objects (no WPF-Freezable-style
        // lock) - but that's an assumption about a third-party library's internals, not a language
        // guarantee, so a defensive try/catch keeps one unexpectedly-immutable color from taking the
        // whole recolor pass (and everything after it in the caller) down with it.
        try
        {
            if (namedColor.Foreground?.GetColor(null) is { } foreground)
            {
                var adjusted = SyntaxColorAdjuster.AdjustForReadability(foreground, editorBackground, muted);
                namedColor.Foreground = new SimpleHighlightingBrush(adjusted);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
