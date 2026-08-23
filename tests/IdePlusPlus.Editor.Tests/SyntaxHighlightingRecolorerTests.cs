using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

/// <summary>
/// SyntaxColorAdjusterTests verifies the color math in isolation; this instead exercises the actual
/// AvalonEdit integration - the part with real risk, since it rests on an unverified assumption about
/// a third-party library (that a built-in HighlightingColor's Foreground can be reassigned after the
/// definition is loaded). If that assumption is ever wrong, this is what will catch it, rather than
/// the app silently falling back to the original low-contrast palette at runtime.
/// </summary>
public sealed class SyntaxHighlightingRecolorerTests
{
    private static readonly Color DarkEditorBackground = Color.FromRgb(30, 30, 30);

    [Fact]
    public void ApplyTheme_OnDarkBackground_RecolorsAtLeastOneBuiltInCSharpToken()
    {
        var definition = HighlightingManager.Instance.GetDefinition("C#");
        definition.ShouldNotBeNull();

        var before = definition.NamedHighlightingColors
            .Select(c => c.Foreground?.GetColor(null))
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToList();
        before.ShouldNotBeEmpty("the built-in C# definition should define at least one named foreground color");

        SyntaxHighlightingRecolorer.ApplyTheme(DarkEditorBackground, muted: false);

        var after = definition.NamedHighlightingColors
            .Select(c => c.Foreground?.GetColor(null))
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToList();

        // At least one color that was too dark for the dark background must actually have changed -
        // proving the mutation reached the real, shared AvalonEdit definition instance, not just a
        // local copy of the palette.
        after.ShouldNotBe(before);
    }

    [Fact]
    public void ApplyTheme_OnDarkBackground_EveryRecoloredTokenMeetsMinimumContrast()
    {
        var definition = HighlightingManager.Instance.GetDefinition("C#");
        definition.ShouldNotBeNull();

        SyntaxHighlightingRecolorer.ApplyTheme(DarkEditorBackground, muted: false);

        foreach (var namedColor in definition.NamedHighlightingColors)
        {
            if (namedColor.Foreground?.GetColor(null) is not { } foreground)
            {
                continue;
            }

            ContrastRatio(foreground, DarkEditorBackground).ShouldBeGreaterThanOrEqualTo(
                4.5, $"named color '{namedColor.Name}' should be legible against the dark editor background");
        }
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var lumA = RelativeLuminance(a);
        var lumB = RelativeLuminance(b);
        var lighter = Math.Max(lumA, lumB);
        var darker = Math.Min(lumA, lumB);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        double Linearize(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
    }
}
