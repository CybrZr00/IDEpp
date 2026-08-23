using System.Windows.Controls;

namespace IdePlusPlus.Shell;

/// <summary>One tool window a plugin registered via IPluginHost.RegisterToolWindow.</summary>
public sealed record PluginToolWindow(string PluginName, string Title, Func<UserControl> Factory);

/// <summary>
/// Tracks tool windows plugins register, keyed by the owning plugin's name so a future "unload
/// plugin X" path could remove everything X added (not wired to any UI yet - see PluginLoader's
/// TryUnload doc comment for the same "exists ahead of its UI" rationale). AvalonDock's docking
/// layout is 100% static XAML today (see MainWindow.xaml/.xaml.cs); the realistic MVP mechanism for
/// a runtime-registered tool window is adding a new LayoutAnchorable to the existing left-hand
/// LayoutAnchorablePane, which is why this class is event-based rather than owning any UI itself -
/// MainWindow.xaml.cs (or whatever future code wires panes) subscribes to <see cref="Registered"/>
/// and builds the actual LayoutAnchorable there.
/// </summary>
public sealed class PluginToolWindowRegistry
{
    private readonly List<PluginToolWindow> _toolWindows = [];

    public IReadOnlyList<PluginToolWindow> ToolWindows => _toolWindows;

    /// <summary>Raised synchronously from <see cref="Register"/> once the registration is tracked.</summary>
    public event Action<PluginToolWindow>? Registered;

    public void Register(string pluginName, string title, Func<UserControl> factory)
    {
        var toolWindow = new PluginToolWindow(pluginName, title, factory);
        _toolWindows.Add(toolWindow);
        Registered?.Invoke(toolWindow);
    }
}
