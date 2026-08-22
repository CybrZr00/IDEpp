using System.Windows.Media;

namespace IdePlusPlus.Editor;

/// <summary>
/// Ambient theme colors for AvalonEdit-internal rendering classes (breakpoint margin, current-line
/// highlight, ghost text) that don't participate in WPF's DynamicResource system - they draw directly
/// via DrawingContext, so ThemeService pushes color changes here instead. Mirrors the EditorServices
/// static-locator pattern already used for reaching app-wide singletons from WPF-templated controls.
/// </summary>
public static class EditorTheme
{
    public static bool IsDark { get; private set; }

    public static Brush Background { get; private set; } = Brushes.White;

    public static Brush Foreground { get; private set; } = Brushes.Black;

    public static Brush CurrentLineHighlight { get; private set; } = new SolidColorBrush(Color.FromArgb(90, 255, 215, 0));

    public static Brush BreakpointMarginBackground { get; private set; } = Brushes.WhiteSmoke;

    public static Brush BreakpointDot { get; private set; } = Brushes.Firebrick;

    public static Brush GhostTextForeground { get; private set; } = Brushes.Gray;

    public static event Action? Changed;

    public static void Apply(bool dark)
    {
        IsDark = dark;

        if (dark)
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220));
            CurrentLineHighlight = new SolidColorBrush(Color.FromArgb(60, 255, 215, 0));
            BreakpointMarginBackground = new SolidColorBrush(Color.FromRgb(37, 37, 38));
            BreakpointDot = new SolidColorBrush(Color.FromRgb(228, 84, 84));
            GhostTextForeground = new SolidColorBrush(Color.FromRgb(140, 140, 140));
        }
        else
        {
            Background = Brushes.White;
            Foreground = Brushes.Black;
            CurrentLineHighlight = new SolidColorBrush(Color.FromArgb(90, 255, 215, 0));
            BreakpointMarginBackground = Brushes.WhiteSmoke;
            BreakpointDot = Brushes.Firebrick;
            GhostTextForeground = Brushes.Gray;
        }

        foreach (var brush in new[] { Background, Foreground, CurrentLineHighlight, BreakpointMarginBackground, BreakpointDot, GhostTextForeground })
        {
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }
        }

        Changed?.Invoke();
    }
}
