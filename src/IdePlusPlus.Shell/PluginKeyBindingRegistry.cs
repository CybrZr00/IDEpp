using System.Windows.Input;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

/// <summary>One keyboard shortcut a plugin registered via IPluginHost.RegisterKeyBinding, accepted
/// (i.e. not colliding with a reserved gesture).</summary>
public sealed record PluginKeyBinding(string PluginName, string GestureText, string Header, Action Execute);

/// <summary>
/// Tracks keyboard shortcuts plugins register, keyed by the owning plugin's name (same "ahead of a
/// future unload path" rationale as <see cref="PluginToolWindowRegistry"/>), and rejects any request
/// that collides with a gesture MainWindow.xaml's static Window.InputBindings (or its Edit/View menu
/// items backed by WPF's ApplicationCommands) already own. A plugin never silently shadows core IDE
/// chrome - a colliding request is refused and logged, not registered.
/// </summary>
public sealed class PluginKeyBindingRegistry
{
    /// <summary>
    /// Mirrors MainWindow.xaml's Window.InputBindings (F5, Ctrl+F5, F10, F11, Shift+F11, Ctrl+G, the
    /// zoom shortcuts) plus the Edit menu's Find/Replace commands (ApplicationCommands.Find/Replace,
    /// Ctrl+F/Ctrl+H) - every gesture that already drives core IDE chrome as of this writing. Kept as
    /// (Key, ModifierKeys) pairs rather than gesture strings so comparison doesn't depend on
    /// formatting (e.g. "Control" vs "Ctrl").
    /// </summary>
    private static readonly HashSet<(Key Key, ModifierKeys Modifiers)> ReservedGestures =
    [
        (Key.F5, ModifierKeys.None),          // Start Debugging
        (Key.F5, ModifierKeys.Control),       // Continue
        (Key.F10, ModifierKeys.None),         // Step Over
        (Key.F11, ModifierKeys.None),         // Step Into
        (Key.F11, ModifierKeys.Shift),        // Step Out
        (Key.G, ModifierKeys.Control),        // Go to Line
        (Key.F, ModifierKeys.Control),        // Find
        (Key.H, ModifierKeys.Control),        // Replace
        (Key.OemPlus, ModifierKeys.Control),  // Zoom In
        (Key.OemMinus, ModifierKeys.Control), // Zoom Out
        (Key.D0, ModifierKeys.Control),       // Reset Zoom
    ];

    private readonly OutputPaneViewModel _output;
    private readonly List<PluginKeyBinding> _bindings = [];

    public PluginKeyBindingRegistry(OutputPaneViewModel output) => _output = output;

    public IReadOnlyList<PluginKeyBinding> Bindings => _bindings;

    /// <summary>Raised synchronously from a successful <see cref="TryRegister"/> once the
    /// registration is tracked.</summary>
    public event Action<PluginKeyBinding>? Registered;

    /// <summary>
    /// Returns false (and logs to the Output pane instead of registering) when
    /// <paramref name="gestureText"/> doesn't parse as a <see cref="KeyGesture"/>, or when it
    /// collides with <see cref="ReservedGestures"/>.
    /// </summary>
    public bool TryRegister(string pluginName, string gestureText, string header, Action execute)
    {
        KeyGesture gesture;
        try
        {
            if (new KeyGestureConverter().ConvertFromString(gestureText) is not KeyGesture parsed)
            {
                throw new NotSupportedException($"'{gestureText}' did not parse to a KeyGesture.");
            }

            gesture = parsed;
        }
        catch (Exception)
        {
            _output.AppendLine($"[Plugin] {pluginName} requested key binding '{gestureText}' for '{header}', which isn't a recognized key gesture - ignored.");
            return false;
        }

        if (IsReserved(gesture))
        {
            _output.AppendLine($"[Plugin] {pluginName} requested key binding '{gestureText}' for '{header}', which is reserved by IDE++ - ignored.");
            return false;
        }

        var binding = new PluginKeyBinding(pluginName, gestureText, header, execute);
        _bindings.Add(binding);
        Registered?.Invoke(binding);
        return true;
    }

    private static bool IsReserved(KeyGesture gesture) => ReservedGestures.Contains((gesture.Key, gesture.Modifiers));
}
