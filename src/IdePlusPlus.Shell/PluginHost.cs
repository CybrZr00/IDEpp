using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using IdePlusPlus.Editor;
using IdePlusPlus.PluginContracts;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

public sealed class PluginHost : IPluginHost
{
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly OutputPaneViewModel _output;
    private readonly PluginToolWindowRegistry _toolWindowRegistry;
    private readonly PluginKeyBindingRegistry _keyBindingRegistry;
    private readonly PluginChatToolRegistry _chatToolRegistry;

    private readonly List<(string PluginName, string Header, Func<EditorContextMenuContext, bool> CanExecute, Action<EditorContextMenuContext> Execute)> _editorContextMenuCommands = [];
    private readonly List<(string PluginName, string Header, Func<SolutionExplorerContextMenuContext, bool> CanExecute, Action<SolutionExplorerContextMenuContext> Execute)> _solutionExplorerContextMenuCommands = [];
    private readonly Dictionary<string, IPluginHoverProvider> _hoverProviders = new(StringComparer.OrdinalIgnoreCase);

    // Set by PluginLoader immediately before/after each plugin's Initialize() call - see SetCurrentPlugin's
    // doc comment. Every Register* call below happens synchronously during Initialize in every known
    // plugin (v1 and v2 alike), so this ambient value is reliably the caller's plugin name at the point
    // any of the methods below runs.
    private string _currentPluginName = UnknownPluginName;
    private const string UnknownPluginName = "(unknown plugin)";

    public PluginHost(
        MainWindowViewModel mainWindowViewModel,
        OutputPaneViewModel output,
        PluginToolWindowRegistry toolWindowRegistry,
        PluginKeyBindingRegistry keyBindingRegistry,
        PluginChatToolRegistry chatToolRegistry)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _output = output;
        _toolWindowRegistry = toolWindowRegistry;
        _keyBindingRegistry = keyBindingRegistry;
        _chatToolRegistry = chatToolRegistry;
    }

    /// <summary>Every editor context-menu command registered so far, with <c>CanExecute</c>/<c>Execute</c>
    /// already wrapped so a throwing plugin delegate degrades to "disabled"/"logged", never a crash.
    /// Not consumed by any editor UI yet - see this feature's design doc for scope.</summary>
    public IReadOnlyList<(string PluginName, string Header, Func<EditorContextMenuContext, bool> CanExecute, Action<EditorContextMenuContext> Execute)> EditorContextMenuCommands => _editorContextMenuCommands;

    /// <summary>Same as <see cref="EditorContextMenuCommands"/> but for Solution Explorer.</summary>
    public IReadOnlyList<(string PluginName, string Header, Func<SolutionExplorerContextMenuContext, bool> CanExecute, Action<SolutionExplorerContextMenuContext> Execute)> SolutionExplorerContextMenuCommands => _solutionExplorerContextMenuCommands;

    /// <summary>Registered hover providers keyed by file extension. Not consumed by the editor yet -
    /// see this feature's design doc for scope.</summary>
    public IReadOnlyDictionary<string, IPluginHoverProvider> HoverProviders => _hoverProviders;

    /// <summary>
    /// Called by PluginLoader to record which plugin's Initialize() is currently running (and to
    /// clear it again once it returns), so the Register* methods below - which take no plugin-name
    /// parameter of their own, since that would mean changing IPluginHost's method signatures per
    /// plugin - can still attribute each registration to its owning plugin. Internal: this is loader
    /// plumbing, not a capability exposed to plugins themselves.
    /// </summary>
    internal void SetCurrentPlugin(string? pluginName) => _currentPluginName = pluginName ?? UnknownPluginName;

    public void RegisterHighlighting(string fileExtension, string xshdFilePath) =>
        HighlightingRegistry.RegisterCustom(fileExtension, xshdFilePath);

    public void RegisterToolsMenuCommand(string header, Action execute) =>
        _mainWindowViewModel.AddPluginCommand(header, () => RunPluginCommand(header, execute));

    public void LogInfo(string message) => _output.AppendLine($"[Plugin] {message}");

    public void RegisterEditorContextMenuCommand(string header, Func<EditorContextMenuContext, bool> canExecute, Action<EditorContextMenuContext> execute)
    {
        var pluginName = _currentPluginName;
        _editorContextMenuCommands.Add((
            pluginName,
            header,
            context => SafeCanExecute(pluginName, header, canExecute, context),
            context => SafeExecute(pluginName, header, execute, context)));
    }

    public void RegisterSolutionExplorerContextMenuCommand(string header, Func<SolutionExplorerContextMenuContext, bool> canExecute, Action<SolutionExplorerContextMenuContext> execute)
    {
        var pluginName = _currentPluginName;
        _solutionExplorerContextMenuCommands.Add((
            pluginName,
            header,
            context => SafeCanExecute(pluginName, header, canExecute, context),
            context => SafeExecute(pluginName, header, execute, context)));
    }

    public void RegisterToolWindow(string title, Func<UserControl> factory)
    {
        var pluginName = _currentPluginName;
        _toolWindowRegistry.Register(pluginName, title, () => SafeCreateToolWindow(pluginName, title, factory));
    }

    public void RegisterHoverProvider(string fileExtension, IPluginHoverProvider provider) =>
        _hoverProviders[fileExtension] = new SafeHoverProvider(_currentPluginName, provider, _output);

    public void RegisterKeyBinding(string gestureText, string header, Action execute)
    {
        var pluginName = _currentPluginName;
        _keyBindingRegistry.TryRegister(pluginName, gestureText, header, () => SafeExecute(pluginName, header, execute));
    }

    public void RegisterChatTool(PluginToolDefinition definition, Func<JsonNode?, CancellationToken, Task<PluginToolResult>> execute) =>
        _chatToolRegistry.Register(_currentPluginName, definition, execute);

    // PluginLoader guards a plugin's Initialize() call, but nothing previously guarded a command
    // registered via RegisterToolsMenuCommand once wired into the menu - a throw from here reached
    // the global DispatcherUnhandledException handler with no plugin-specific context.
    private void RunPluginCommand(string header, Action execute)
    {
        try
        {
            execute();
        }
        catch (Exception ex)
        {
            _output.AppendLine($"[Plugin] Command '{header}' threw: {ex}");
        }
    }

    private void SafeExecute(string pluginName, string label, Action execute)
    {
        try
        {
            execute();
        }
        catch (Exception ex)
        {
            _output.AppendLine($"[Plugin] {pluginName}'s '{label}' threw: {ex}");
        }
    }

    private bool SafeCanExecute<TContext>(string pluginName, string label, Func<TContext, bool> canExecute, TContext context)
    {
        try
        {
            return canExecute(context);
        }
        catch (Exception ex)
        {
            _output.AppendLine($"[Plugin] {pluginName}'s '{label}' canExecute threw: {ex}");
            return false;
        }
    }

    private void SafeExecute<TContext>(string pluginName, string label, Action<TContext> execute, TContext context)
    {
        try
        {
            execute(context);
        }
        catch (Exception ex)
        {
            _output.AppendLine($"[Plugin] {pluginName}'s '{label}' threw: {ex}");
        }
    }

    private UserControl SafeCreateToolWindow(string pluginName, string title, Func<UserControl> factory)
    {
        try
        {
            return factory();
        }
        catch (Exception ex)
        {
            _output.AppendLine($"[Plugin] {pluginName}'s tool window '{title}' failed to create: {ex}");
            return new UserControl
            {
                Content = new TextBlock
                {
                    Text = $"Plugin tool window '{title}' failed to load. See Output for details.",
                    Margin = new Thickness(8),
                    TextWrapping = TextWrapping.Wrap,
                },
            };
        }
    }

    /// <summary>Wraps a plugin's <see cref="IPluginHoverProvider"/> so a throwing implementation
    /// degrades to "no hover text" (and a logged line) instead of breaking hover for every other
    /// registered extension.</summary>
    private sealed class SafeHoverProvider : IPluginHoverProvider
    {
        private readonly string _pluginName;
        private readonly IPluginHoverProvider _inner;
        private readonly OutputPaneViewModel _output;

        public SafeHoverProvider(string pluginName, IPluginHoverProvider inner, OutputPaneViewModel output)
        {
            _pluginName = pluginName;
            _inner = inner;
            _output = output;
        }

        public async Task<string?> GetHoverTextAsync(PluginHoverContext context, CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.GetHoverTextAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                _output.AppendLine($"[Plugin] {_pluginName}'s hover provider threw: {ex}");
                return null;
            }
        }
    }
}
