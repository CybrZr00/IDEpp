namespace IdePlusPlus.Editor;

/// <summary>
/// Ambient access to a couple of app-wide singletons for TextEditorHost, which WPF constructs via
/// an implicit DataTemplate (a parameterless constructor) rather than DI. App.xaml.cs populates
/// this right after building the service provider, before the first document is opened.
/// </summary>
public static class EditorServices
{
    public static BreakpointManager BreakpointManager { get; set; } = null!;

    public static DebugLocationIndicator DebugLocation { get; set; } = null!;

    public static CaretPositionIndicator CaretPosition { get; set; } = null!;

    public static GoToLineRequest GoToLine { get; set; } = null!;

    /// <summary>Null until an inline completion provider (e.g. DeepSeek FIM) has a usable API key configured.</summary>
    public static IInlineCompletionProvider? CompletionProvider { get; set; }
}
