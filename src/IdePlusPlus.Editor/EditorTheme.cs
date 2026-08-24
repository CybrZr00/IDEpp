using System.Windows.Media;

namespace IdePlusPlus.Editor;

/// <summary>
/// Ambient theme colors for AvalonEdit-internal rendering classes (breakpoint margin, current-line
/// highlight, ghost text) that don't participate in WPF's DynamicResource system - they draw directly
/// via DrawingContext, so ThemeService pushes color changes here instead. Mirrors the EditorServices
/// static-locator pattern already used for reaching app-wide singletons from WPF-templated controls.
/// Also drives SyntaxHighlightingRecolorer, so switching themes retints AvalonEdit's built-in
/// per-language syntax colors (keywords/strings/comments/...) to stay legible against whichever
/// background is now active, not just the editor's own background/foreground.
/// </summary>
public static class EditorTheme
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>True for the two dark-background presets (Dark, SoftDark) - used where a caller only
    /// needs "is this a dark theme" rather than which one exactly (e.g. picking AvalonDock's own
    /// built-in light/dark theme, which has no equivalent of the app's full theme set).</summary>
    public static bool IsDark => Current is AppTheme.Dark or AppTheme.SoftDark;

    public static Brush Background { get; private set; } = Brushes.White;

    public static Brush Foreground { get; private set; } = Brushes.Black;

    /// <summary>The debugger's paused-execution-line highlight - bold/attention-grabbing by design,
    /// distinct from the much subtler CaretLineHighlight below.</summary>
    public static Brush CurrentLineHighlight { get; private set; } = new SolidColorBrush(Color.FromArgb(90, 255, 215, 0));

    /// <summary>A quiet, always-on tint under whichever line the caret is on - a constant "you are
    /// here" anchor in a long file, independent of debugging. Deliberately much lower-contrast than
    /// CurrentLineHighlight so the two never compete when a debug session and the caret land on the
    /// same line.</summary>
    public static Brush CaretLineHighlight { get; private set; } = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));

    public static Brush BreakpointMarginBackground { get; private set; } = Brushes.WhiteSmoke;

    public static Brush BreakpointDot { get; private set; } = Brushes.Firebrick;

    public static Brush GhostTextForeground { get; private set; } = Brushes.Gray;

    public static Brush DiagnosticErrorSquiggle { get; private set; } = new SolidColorBrush(Color.FromRgb(228, 84, 84));

    public static Brush DiagnosticWarningSquiggle { get; private set; } = new SolidColorBrush(Color.FromRgb(212, 168, 44));

    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var palette = GetPalette(theme);

        Background = Freeze(new SolidColorBrush(palette.Background));
        Foreground = Freeze(new SolidColorBrush(palette.Foreground));
        CurrentLineHighlight = Freeze(new SolidColorBrush(palette.CurrentLineHighlight));
        CaretLineHighlight = Freeze(new SolidColorBrush(palette.CaretLineHighlight));
        BreakpointMarginBackground = Freeze(new SolidColorBrush(palette.BreakpointMarginBackground));
        BreakpointDot = Freeze(new SolidColorBrush(palette.BreakpointDot));
        GhostTextForeground = Freeze(new SolidColorBrush(palette.GhostTextForeground));
        DiagnosticErrorSquiggle = Freeze(new SolidColorBrush(palette.DiagnosticErrorSquiggle));
        DiagnosticWarningSquiggle = Freeze(new SolidColorBrush(palette.DiagnosticWarningSquiggle));

        SyntaxHighlightingRecolorer.ApplyTheme(palette.Background, palette.Muted);

        Changed?.Invoke();
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }

    private readonly record struct Palette(
        Color Background,
        Color Foreground,
        Color CurrentLineHighlight,
        Color CaretLineHighlight,
        Color BreakpointMarginBackground,
        Color BreakpointDot,
        Color GhostTextForeground,
        Color DiagnosticErrorSquiggle,
        Color DiagnosticWarningSquiggle,
        bool Muted);

    private static Palette GetPalette(AppTheme theme) => theme switch
    {
        AppTheme.Dark => new Palette(
            Background: Color.FromRgb(30, 30, 30),
            Foreground: Color.FromRgb(220, 220, 220),
            CurrentLineHighlight: Color.FromArgb(60, 255, 215, 0),
            CaretLineHighlight: Color.FromArgb(22, 255, 255, 255),
            BreakpointMarginBackground: Color.FromRgb(37, 37, 38),
            BreakpointDot: Color.FromRgb(228, 84, 84),
            GhostTextForeground: Color.FromRgb(140, 140, 140),
            DiagnosticErrorSquiggle: Color.FromRgb(240, 100, 100),
            DiagnosticWarningSquiggle: Color.FromRgb(216, 178, 74),
            Muted: false),

        // A warmer, lower-contrast dark theme than Dark - a near-black-but-neutral background reads
        // as harsher/more fatiguing over long sessions than a softened, faintly warm dark gray, and
        // pure white text against near-black is itself a glare source; softening both plus muting the
        // syntax palette's saturation is the "easier to focus on" soft-dark look.
        AppTheme.SoftDark => new Palette(
            Background: Color.FromRgb(0x2B, 0x2B, 0x28),
            Foreground: Color.FromRgb(0xD4, 0xCF, 0xC4),
            CurrentLineHighlight: Color.FromArgb(55, 235, 175, 60),
            CaretLineHighlight: Color.FromArgb(18, 255, 255, 255),
            BreakpointMarginBackground: Color.FromRgb(0x30, 0x2F, 0x2B),
            BreakpointDot: Color.FromRgb(0xD9, 0x77, 0x6B),
            GhostTextForeground: Color.FromRgb(0x94, 0x8C, 0x7E),
            DiagnosticErrorSquiggle: Color.FromRgb(0xD9, 0x7B, 0x72),
            DiagnosticWarningSquiggle: Color.FromRgb(0xC7, 0xA2, 0x5A),
            Muted: true),

        // An off-white/cream background (rather than stark white) and a soft warm-charcoal foreground
        // (rather than pure black) both reduce the high-frequency black/white "vibration" many
        // dyslexic readers report; the syntax palette is muted for the same reason bright saturated
        // colors are - lower visual noise, easier to keep your place.
        AppTheme.DyslexiaFriendly => new Palette(
            Background: Color.FromRgb(0xFA, 0xF3, 0xE3),
            Foreground: Color.FromRgb(0x3B, 0x35, 0x29),
            CurrentLineHighlight: Color.FromArgb(80, 235, 175, 60),
            CaretLineHighlight: Color.FromArgb(20, 0, 0, 0),
            BreakpointMarginBackground: Color.FromRgb(0xF1, 0xE7, 0xCF),
            BreakpointDot: Color.FromRgb(0xB5, 0x48, 0x4A),
            GhostTextForeground: Color.FromRgb(0x8A, 0x81, 0x71),
            DiagnosticErrorSquiggle: Color.FromRgb(0xC5, 0x50, 0x4F),
            DiagnosticWarningSquiggle: Color.FromRgb(0xB0, 0x8A, 0x2E),
            Muted: true),

        _ => new Palette(
            Background: Colors.White,
            Foreground: Colors.Black,
            CurrentLineHighlight: Color.FromArgb(90, 255, 215, 0),
            CaretLineHighlight: Color.FromArgb(20, 0, 0, 0),
            BreakpointMarginBackground: Colors.WhiteSmoke,
            BreakpointDot: Colors.Firebrick,
            GhostTextForeground: Colors.Gray,
            DiagnosticErrorSquiggle: Color.FromRgb(228, 84, 84),
            DiagnosticWarningSquiggle: Color.FromRgb(212, 168, 44),
            Muted: false),
    };
}
