using System.Windows.Media;
using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

/// <summary>
/// AvalonEdit's built-in highlighting definitions ship one palette designed for a white background
/// (dark navy keywords, dark green comments, ...) - these verify SyntaxColorAdjuster actually fixes
/// the reported "dark blues and blacks on the dark background" contrast bug, using the real WCAG
/// contrast-ratio formula rather than trusting the production code's own math.
/// </summary>
public sealed class SyntaxColorAdjusterTests
{
    private static readonly Color DarkEditorBackground = Color.FromRgb(30, 30, 30); // #1E1E1E, matches EditorTheme.Dark
    private static readonly Color LightEditorBackground = Colors.White;

    // WCAG 2.1 AA minimum for normal-size text - see SyntaxColorAdjuster's own remarks for why.
    private const double MinimumContrastRatio = 4.5;

    [Theory]
    [InlineData(0, 0, 255)] // pure blue - a typical "keyword" color; HSL lightness alone (0.5) hides
                             // how dark it actually reads, which is exactly why this needs a real
                             // WCAG contrast check rather than a lightness threshold.
    [InlineData(0, 0, 139)] // navy
    [InlineData(0, 128, 0)] // dark green - a typical "comment" color
    [InlineData(163, 21, 21)] // dark red - a typical "string" color
    [InlineData(0, 0, 0)] // black - a typical unstyled/"default" color
    public void AdjustForReadability_OnDarkBackground_MeetsMinimumContrast(byte r, byte g, byte b)
    {
        var original = Color.FromRgb(r, g, b);

        var adjusted = SyntaxColorAdjuster.AdjustForReadability(original, DarkEditorBackground, muted: false);

        ContrastRatio(adjusted, DarkEditorBackground).ShouldBeGreaterThanOrEqualTo(MinimumContrastRatio);
    }

    [Theory]
    [InlineData(0, 0, 255)]
    [InlineData(0, 128, 0)]
    [InlineData(163, 21, 21)]
    public void AdjustForReadability_AlreadyLegibleOnLightBackground_LeavesColorUnchanged(byte r, byte g, byte b)
    {
        var original = Color.FromRgb(r, g, b);

        var adjusted = SyntaxColorAdjuster.AdjustForReadability(original, LightEditorBackground, muted: false);

        // AvalonEdit's built-in palette was authored for a white background, so these should already
        // clear the bar and come back untouched - the light theme was never the reported bug.
        adjusted.ShouldBe(original);
    }

    [Fact]
    public void AdjustForReadability_OnDarkBackground_PreservesHueSoTokensStayRecognizable()
    {
        var blue = Color.FromRgb(0, 0, 255);

        var adjusted = SyntaxColorAdjuster.AdjustForReadability(blue, DarkEditorBackground, muted: false);

        // Lifted for contrast, but still clearly "blue-ish": far more blue channel than red or green.
        adjusted.B.ShouldBeGreaterThan(adjusted.R);
        adjusted.B.ShouldBeGreaterThan(adjusted.G);
        adjusted.R.ShouldBe(adjusted.G); // pure blue has no red/green skew, so lifting shouldn't add one
    }

    [Fact]
    public void AdjustForReadability_PreservesAlpha()
    {
        var translucentRed = Color.FromArgb(128, 163, 21, 21);

        var adjusted = SyntaxColorAdjuster.AdjustForReadability(translucentRed, DarkEditorBackground, muted: false);

        adjusted.A.ShouldBe((byte)128);
    }

    [Fact]
    public void AdjustForReadability_Muted_IsLessSaturatedThanUnmuted()
    {
        var blue = Color.FromRgb(0, 0, 255);

        var standard = SyntaxColorAdjuster.AdjustForReadability(blue, DarkEditorBackground, muted: false);
        var muted = SyntaxColorAdjuster.AdjustForReadability(blue, DarkEditorBackground, muted: true);

        // Both muted and standard results are still real hues (blue-dominant); the muted version
        // sits closer to gray - a smaller gap between its max and min channel values.
        (muted.B - muted.R).ShouldBeLessThan(standard.B - standard.R);

        // "Softer" must never mean "harder to read" - muted still has to clear the same bar.
        ContrastRatio(muted, DarkEditorBackground).ShouldBeGreaterThanOrEqualTo(MinimumContrastRatio);
    }

    /// <summary>Independent re-implementation of the WCAG 2.1 contrast-ratio formula, so these tests
    /// verify actual readability rather than re-checking SyntaxColorAdjuster's own arithmetic.</summary>
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
