using System.Windows.Media;

namespace IdePlusPlus.Editor;

/// <summary>
/// AvalonEdit's built-in highlighting definitions (C#, XML, JavaScript, ...) ship one fixed palette
/// designed for a white editor background - keywords/types as dark navy, comments as dark green, and
/// so on. Rendered as-is on a dark background, those same "dark on white" colors become dark blues
/// and near-blacks on near-black - exactly the low-contrast, hard-to-read tokens reported. Plain HSL
/// lightness is a poor proxy for that: pure blue (#0000FF) already sits at HSL lightness 0.5, yet
/// reads as clearly "dark" against a near-black background because blue contributes so little to
/// perceived brightness - so this instead computes the actual WCAG relative-luminance contrast ratio
/// against the real background color and lifts (or, on a light background, lowers) lightness in HSL
/// space - preserving hue, so "keywords are blue-ish, strings are orange-ish" etc. stays intact -
/// until the ratio clears a legible minimum.
/// </summary>
public static class SyntaxColorAdjuster
{
    // WCAG 2.1's AA threshold for normal-size text (the editor's default ~13px font is well under
    // the "large text" carve-out that would allow the lower 3:1 threshold).
    private const double MinimumContrastRatio = 4.5;

    private const double LightnessStep = 0.015;
    private const int MaxIterations = 80;

    /// <summary>
    /// Returns <paramref name="original"/> nudged in HSL lightness/saturation until it contrasts
    /// legibly against <paramref name="background"/>, or returns it unchanged if it already does.
    /// <paramref name="muted"/> softens the result (lower saturation) for the calmer
    /// SoftDark/DyslexiaFriendly presets, rather than the punchier stock palette - it does not relax
    /// the contrast floor, since "softer" should never mean "harder to read".
    /// </summary>
    public static Color AdjustForReadability(Color original, Color background, bool muted)
    {
        var (hue, saturation, lightness) = ToHsl(original);
        if (muted)
        {
            saturation *= 0.72;
        }

        var backgroundIsDark = RelativeLuminance(background) < 0.5;
        var candidate = FromHsl(hue, saturation, lightness, original.A);

        for (var i = 0; i < MaxIterations && ContrastRatio(candidate, background) < MinimumContrastRatio; i++)
        {
            var nextLightness = backgroundIsDark
                ? Math.Min(1.0, lightness + LightnessStep)
                : Math.Max(0.0, lightness - LightnessStep);

            if (nextLightness == lightness)
            {
                break; // hit the lightness extreme (0 or 1) with no more room to move
            }

            lightness = nextLightness;
            candidate = FromHsl(hue, saturation, lightness, original.A);
        }

        return candidate;
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

    private static double ContrastRatio(Color a, Color b)
    {
        var lumA = RelativeLuminance(a);
        var lumB = RelativeLuminance(b);
        var (lighter, darker) = lumA >= lumB ? (lumA, lumB) : (lumB, lumA);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2.0;

        if (max == min)
        {
            return (0, 0, lightness); // achromatic (gray/black/white)
        }

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

        double hue;
        if (max == r)
        {
            hue = ((g - b) / delta) % 6.0;
        }
        else if (max == g)
        {
            hue = ((b - r) / delta) + 2.0;
        }
        else
        {
            hue = ((r - g) / delta) + 4.0;
        }

        hue *= 60.0;
        if (hue < 0)
        {
            hue += 360.0;
        }

        return (hue, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness, byte alpha)
    {
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);

        if (saturation == 0)
        {
            var gray = (byte)Math.Round(lightness * 255.0);
            return Color.FromArgb(alpha, gray, gray, gray);
        }

        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;
        var h = hue / 360.0;

        return Color.FromArgb(
            alpha,
            (byte)Math.Round(HueToChannel(p, q, h + 1.0 / 3.0) * 255.0),
            (byte)Math.Round(HueToChannel(p, q, h) * 255.0),
            (byte)Math.Round(HueToChannel(p, q, h - 1.0 / 3.0) * 255.0));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6.0)
        {
            return p + (q - p) * 6.0 * t;
        }

        if (t < 1.0 / 2.0)
        {
            return q;
        }

        if (t < 2.0 / 3.0)
        {
            return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        }

        return p;
    }
}
