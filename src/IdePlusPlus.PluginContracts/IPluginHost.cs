using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace IdePlusPlus.PluginContracts;

/// <summary>
/// The IDE's side of the plugin contract, passed to IIdePlugin.Initialize. Deliberately small - each
/// method is a narrow, additive capability rather than exposing IDE++'s internals wholesale, so a
/// plugin can't reach into (or break) state it doesn't own.
///
/// IMPORTANT for future maintainers: <see cref="RegisterHighlighting"/>, <see cref="RegisterToolsMenuCommand"/>,
/// and <see cref="LogInfo"/> are the original v1 members and must NEVER change signature - not even
/// by adding an optional parameter - for as long as this interface exists. A plugin DLL compiled
/// against an older version of this assembly already has those exact method signatures baked into
/// its IL as MethodRefs; changing them breaks that binary at load time even though the source-level
/// change looks compatible. Every future change to this interface (and to <see cref="IIdePlugin"/>)
/// must be an addition of a brand-new member, never a modification of an existing one.
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// Registers syntax highlighting for a file extension (e.g. ".ini") from a .xshd file. Takes
    /// priority over IDE++'s own built-in mapping for that extension if there is one, so a plugin
    /// can also override the built-in highlighting for a language it wants to handle differently.
    /// </summary>
    void RegisterHighlighting(string fileExtension, string xshdFilePath);

    /// <summary>Adds a command under Tools > Plugins.</summary>
    void RegisterToolsMenuCommand(string header, Action execute);

    /// <summary>Writes a line to the Output pane, prefixed to identify it as coming from a plugin.</summary>
    void LogInfo(string message);

    /// <summary>
    /// Adds a command to the editor's right-click context menu. <paramref name="canExecute"/> is
    /// evaluated each time the menu is about to be shown to decide whether the item is enabled;
    /// <paramref name="execute"/> runs when it's clicked. Both are called with a snapshot of the
    /// document/caret state at that moment.
    /// </summary>
    void RegisterEditorContextMenuCommand(string header, Func<EditorContextMenuContext, bool> canExecute, Action<EditorContextMenuContext> execute);

    /// <summary>
    /// Adds a command to Solution Explorer's right-click context menu, evaluated/invoked the same
    /// way as <see cref="RegisterEditorContextMenuCommand"/> but against the right-clicked tree node.
    /// </summary>
    void RegisterSolutionExplorerContextMenuCommand(string header, Func<SolutionExplorerContextMenuContext, bool> canExecute, Action<SolutionExplorerContextMenuContext> execute);

    /// <summary>
    /// Registers a dockable tool window with the given title. <paramref name="factory"/> is called
    /// to create the window's content - once per registration for the MVP (multiple tool windows per
    /// plugin, and re-creating content across sessions/layout restores, are both deferred).
    /// </summary>
    void RegisterToolWindow(string title, Func<UserControl> factory);

    /// <summary>
    /// Registers an editor hover provider for a file extension (e.g. ".cs"). When the mouse comes to
    /// rest over a word in a matching document, <see cref="IPluginHoverProvider.GetHoverTextAsync"/>
    /// is asked for text to show in a tooltip; returning null shows nothing.
    /// </summary>
    void RegisterHoverProvider(string fileExtension, IPluginHoverProvider provider);

    /// <summary>
    /// Registers a global keyboard shortcut. <paramref name="gestureText"/> uses the same format
    /// WPF's <see cref="System.Windows.Input.KeyGestureConverter"/> parses (e.g. "Ctrl+Shift+P"). A
    /// gesture that collides with one of IDE++'s own reserved shortcuts (F5, Ctrl+F5, F10, F11,
    /// Shift+F11, Ctrl+F, Ctrl+G, Ctrl+H, and the zoom shortcuts) is rejected - it is never silently
    /// overridden - and logged instead of registered.
    /// </summary>
    void RegisterKeyBinding(string gestureText, string header, Action execute);

    /// <summary>
    /// Registers a tool the chat assistant can call, alongside its built-in tools and any tools from
    /// configured MCP servers. IDE++ exposes it to the assistant under the qualified name
    /// <c>"plugin__{PluginName}__{definition.Name}"</c> so it can never collide with another
    /// plugin's or an MCP server's tool of the same short name. <paramref name="execute"/> receives
    /// the model-supplied arguments (or null) and must return a result rather than throw - IDE++
    /// treats an exception from it as an error result, not a crash, but a plugin that returns a
    /// clear <see cref="PluginToolResult"/> with <c>IsError: true</c> gives the model better
    /// information to react to.
    /// </summary>
    void RegisterChatTool(PluginToolDefinition definition, Func<JsonNode?, CancellationToken, Task<PluginToolResult>> execute);

    /// <summary>
    /// Registers a handler to run at a point in the build lifecycle, alongside whatever Task
    /// Explorer has bound to the same point. Handlers for one event run one after another, in
    /// registration order, and the build waits for them.
    ///
    /// A handler that throws is logged and skipped: it does not fail the build, on the same
    /// reasoning as everywhere else here - a broken plugin must not be able to make the IDE unable
    /// to build. One that hangs is abandoned after a timeout for the same reason, so a handler that
    /// never returns costs one slow build rather than every future one. Neither the handler's
    /// outcome nor its duration is reported back to the plugin.
    ///
    /// The <paramref name="handler"/>'s CancellationToken is cancelled on that timeout; a
    /// well-behaved handler should honour it.
    /// </summary>
    void RegisterBuildEventHandler(BuildEventKind kind, Func<BuildEventContext, CancellationToken, Task> handler);

    /// <summary>
    /// Asks the IDE's configured inline (fill-in-the-middle) completion model - the same one that
    /// produces ghost-text suggestions in IDE++'s own editor - for what should come next between
    /// <paramref name="prefix"/> and <paramref name="suffix"/>. Returns null when no such model is
    /// configured, or it has nothing to suggest. Rendering the suggestion is the plugin's own job:
    /// this exists so a plugin that hosts its own editor (a tool window with an embedded AvalonEdit,
    /// say) can offer the same completions without reaching into IDE++'s editor internals.
    /// </summary>
    Task<string?> GetInlineCompletionAsync(string prefix, string suffix, CancellationToken cancellationToken);
}
