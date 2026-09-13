using System.Text.Json.Nodes;
using System.Windows.Controls;
using IdePlusPlus.PluginContracts;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// A minimal, dependency-free <see cref="IPluginHost"/> for tests, standing in for the real
/// <c>PluginHost</c> (which requires a live <c>MainWindowViewModel</c> and therefore a running WPF
/// <c>Application</c> to construct). Records every call so tests can assert a plugin actually invoked
/// the host - including, critically, that the call succeeds at all: a plugin running inside its own
/// <see cref="PluginLoadContext"/> calling a method on an <see cref="IPluginHost"/> instance created in
/// the Default ALC is exactly the cross-ALC interface dispatch that breaks if PluginLoadContext ever
/// fails to forward IdePlusPlus.PluginContracts to the Default ALC.
///
/// The v2 members below just record their calls the same way the v1 members do - none of the
/// existing loader tests exercise them (they're covered directly against the real registries in
/// PluginKeyBindingRegistryTests/PluginChatToolRegistryTests instead), but this class still has to
/// implement all of IPluginHost to compile as an IPluginHost at all.
/// </summary>
internal sealed class RecordingPluginHost : IPluginHost
{
    public List<(string FileExtension, string XshdFilePath)> RegisteredHighlighting { get; } = [];

    public List<(string Header, Action Execute)> RegisteredToolsMenuCommands { get; } = [];

    public List<string> LoggedMessages { get; } = [];

    public List<(string Header, Func<EditorContextMenuContext, bool> CanExecute, Action<EditorContextMenuContext> Execute)> RegisteredEditorContextMenuCommands { get; } = [];

    public List<(string Header, Func<SolutionExplorerContextMenuContext, bool> CanExecute, Action<SolutionExplorerContextMenuContext> Execute)> RegisteredSolutionExplorerContextMenuCommands { get; } = [];

    public List<(string Title, Func<UserControl> Factory)> RegisteredToolWindows { get; } = [];

    public List<(string FileExtension, IPluginHoverProvider Provider)> RegisteredHoverProviders { get; } = [];

    public List<(string GestureText, string Header, Action Execute)> RegisteredKeyBindings { get; } = [];

    public List<(PluginToolDefinition Definition, Func<JsonNode?, CancellationToken, Task<PluginToolResult>> Execute)> RegisteredChatTools { get; } = [];

    public List<(BuildEventKind Kind, Func<BuildEventContext, CancellationToken, Task> Handler)> RegisteredBuildEventHandlers { get; } = [];

    public void RegisterHighlighting(string fileExtension, string xshdFilePath) =>
        RegisteredHighlighting.Add((fileExtension, xshdFilePath));

    public void RegisterToolsMenuCommand(string header, Action execute) =>
        RegisteredToolsMenuCommands.Add((header, execute));

    public void LogInfo(string message) => LoggedMessages.Add(message);

    public void RegisterEditorContextMenuCommand(string header, Func<EditorContextMenuContext, bool> canExecute, Action<EditorContextMenuContext> execute) =>
        RegisteredEditorContextMenuCommands.Add((header, canExecute, execute));

    public void RegisterSolutionExplorerContextMenuCommand(string header, Func<SolutionExplorerContextMenuContext, bool> canExecute, Action<SolutionExplorerContextMenuContext> execute) =>
        RegisteredSolutionExplorerContextMenuCommands.Add((header, canExecute, execute));

    public void RegisterToolWindow(string title, Func<UserControl> factory) =>
        RegisteredToolWindows.Add((title, factory));

    public List<(string Prefix, string Suffix)> InlineCompletionRequests { get; } = [];

    public Task<string?> GetInlineCompletionAsync(string prefix, string suffix, CancellationToken cancellationToken)
    {
        InlineCompletionRequests.Add((prefix, suffix));
        return Task.FromResult<string?>(null);
    }

    public void RegisterHoverProvider(string fileExtension, IPluginHoverProvider provider) =>
        RegisteredHoverProviders.Add((fileExtension, provider));

    public void RegisterKeyBinding(string gestureText, string header, Action execute) =>
        RegisteredKeyBindings.Add((gestureText, header, execute));

    public void RegisterChatTool(PluginToolDefinition definition, Func<JsonNode?, CancellationToken, Task<PluginToolResult>> execute) =>
        RegisteredChatTools.Add((definition, execute));

    public void RegisterBuildEventHandler(BuildEventKind kind, Func<BuildEventContext, CancellationToken, Task> handler) =>
        RegisteredBuildEventHandlers.Add((kind, handler));
}
