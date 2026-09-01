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

    public static DiffPreviewRequest DiffPreview { get; set; } = null!;

    /// <summary>Null until an inline completion provider (e.g. DeepSeek FIM) has a usable API key configured.</summary>
    public static IInlineCompletionProvider? CompletionProvider { get; set; }

    /// <summary>Null until the Roslyn workspace has been constructed (App.xaml.cs wires this
    /// unconditionally, so in practice this is only null very early in startup or in tests that never
    /// set it).</summary>
    public static ILiveDiagnosticsProvider? DiagnosticsProvider { get; set; }

    /// <summary>Null under the same conditions as <see cref="DiagnosticsProvider"/>.</summary>
    public static IMemberCompletionProvider? MemberCompletionProvider { get; set; }

    /// <summary>
    /// Null until at least one plugin has registered an editor context-menu command. Invoked fresh
    /// each time the editor's context menu is about to open (not cached), since the underlying
    /// registration list can grow as plugins load. Kept as a delegate returning plain
    /// <see cref="EditorContextMenuCommandDescriptor"/> values - not IPluginHost/PluginHost types
    /// directly - so this project doesn't need a reference to IdePlusPlus.PluginContracts or
    /// IdePlusPlus.Shell; App.xaml.cs wires this to the real registry after building the service
    /// provider, converting each PluginHost registration to this shape.
    /// </summary>
    public static Func<IReadOnlyList<EditorContextMenuCommandDescriptor>>? EditorContextMenuCommands { get; set; }

    /// <summary>
    /// Null until at least one plugin has registered a hover provider. Given a file extension (e.g.
    /// ".cs"), returns a delegate that computes hover text for a position in a matching document, or
    /// null if no provider is registered for that extension - looked up fresh per hover, same
    /// reasoning as <see cref="EditorContextMenuCommands"/>.
    /// </summary>
    public static Func<string, Func<EditorHoverRequest, CancellationToken, Task<string?>>?>? HoverProviderLookup { get; set; }
}
