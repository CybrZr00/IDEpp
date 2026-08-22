namespace IdePlusPlus.Editor;

/// <summary>
/// Ambient word-wrap/zoom settings, mirroring EditorTheme's static-broadcast pattern - every open
/// TextEditorHost applies these to its own AvalonEdit TextEditor instance on Changed.
/// </summary>
public static class EditorSettings
{
    public const double DefaultFontSize = 13;
    private const double MinFontSize = 8;
    private const double MaxFontSize = 32;

    public static bool WordWrap { get; private set; }

    public static double FontSize { get; private set; } = DefaultFontSize;

    public static event Action? Changed;

    public static void SetWordWrap(bool value)
    {
        WordWrap = value;
        Changed?.Invoke();
    }

    public static void ToggleWordWrap() => SetWordWrap(!WordWrap);

    public static void SetFontSize(double value)
    {
        FontSize = Math.Clamp(value, MinFontSize, MaxFontSize);
        Changed?.Invoke();
    }

    public static void ZoomIn() => SetFontSize(FontSize + 1);

    public static void ZoomOut() => SetFontSize(FontSize - 1);

    public static void ResetZoom() => SetFontSize(DefaultFontSize);
}
