using System.IO;
using System.Reflection;
using IdePlusPlus.PluginContracts;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

/// <summary>
/// Discovers plugins by simple assembly-probing rather than MEF: every .dll directly under
/// PluginsDirectory is loaded and scanned by reflection for IIdePlugin implementations. Plugins run
/// in-process (not isolated in their own AssemblyLoadContext) - the tradeoff of "simple" over "MEF"
/// per the original M8 plan is that a plugin can't be unloaded at runtime, but for a solo-hobbyist
/// IDE that's loaded once at startup, that's not a real cost. A plugin that fails to load or throws
/// during Initialize is logged and skipped; it never blocks the rest of the IDE from starting.
/// </summary>
public sealed class PluginLoader
{
    public static string PluginsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus",
        "plugins");

    private readonly PluginHost _host;
    private readonly OutputPaneViewModel _output;

    public PluginLoader(PluginHost host, OutputPaneViewModel output)
    {
        _host = host;
        _output = output;
    }

    public void LoadPlugins()
    {
        Directory.CreateDirectory(PluginsDirectory);

        foreach (var dllPath in Directory.GetFiles(PluginsDirectory, "*.dll"))
        {
            try
            {
                LoadPluginsFrom(dllPath);
            }
            catch (Exception ex)
            {
                _output.AppendLine($"[Plugin] Failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }
    }

    private void LoadPluginsFrom(string dllPath)
    {
        var assembly = Assembly.LoadFrom(dllPath);

        var pluginTypes = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IIdePlugin).IsAssignableFrom(t));

        foreach (var type in pluginTypes)
        {
            if (Activator.CreateInstance(type) is not IIdePlugin plugin)
            {
                continue;
            }

            try
            {
                plugin.Initialize(_host);
                _output.AppendLine($"[Plugin] Loaded: {plugin.Name} ({Path.GetFileName(dllPath)})");
            }
            catch (Exception ex)
            {
                _output.AppendLine($"[Plugin] {plugin.Name} threw during Initialize: {ex.Message}");
            }
        }
    }
}
