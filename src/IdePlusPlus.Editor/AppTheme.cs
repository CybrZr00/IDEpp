namespace IdePlusPlus.Editor;

/// <summary>
/// The set of built-in color themes. Each maps to both a chrome ResourceDictionary (Shell's
/// Themes/*.xaml, picked by ThemeService) and an editor/syntax palette (EditorTheme,
/// SyntaxHighlightingRecolorer) - the two halves of "what does the app look like" are kept in sync
/// by always driving both off this one enum rather than two independent bools.
/// </summary>
public enum AppTheme
{
    /// <summary>Clean, high-contrast light theme - the default.</summary>
    Light,

    /// <summary>Near-black background, light foreground - VS Code "Dark+"-style contrast.</summary>
    Dark,

    /// <summary>A warmer, lower-contrast dark theme (softer background, muted syntax colors) aimed
    /// at reduced eye strain and easier sustained focus than the stronger-contrast Dark theme.</summary>
    SoftDark,

    /// <summary>An off-white/cream background with a muted, low-glare syntax palette and wider
    /// letter/line spacing - aimed at readers who find stark white backgrounds and tightly-packed
    /// high-contrast text harder to track (a common dyslexia accommodation).</summary>
    DyslexiaFriendly,
}
