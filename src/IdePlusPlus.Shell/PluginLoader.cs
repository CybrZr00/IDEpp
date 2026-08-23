using System.IO;
using System.Reflection;
using IdePlusPlus.PluginContracts;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

/// <summary>
/// Discovers plugins by simple assembly-probing rather than MEF: every .dll directly under
/// PluginsDirectory is loaded and scanned by reflection for IIdePlugin implementations. Each plugin
/// DLL gets its own collectible <see cref="PluginLoadContext"/> (see that class's doc comment for the
/// full rationale and its explicit non-goals - in short: this buys clean unload/versioning and raises
/// the bar against accidental overreach, it is NOT a security boundary against a hostile plugin). A
/// plugin that fails to load or throws during Initialize is logged and skipped; it never blocks the
/// rest of the IDE from starting.
/// </summary>
public sealed class PluginLoader
{
    public static string PluginsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus",
        "plugins");

    private readonly IPluginHost _host;
    private readonly OutputPaneViewModel _output;
    private readonly List<LoadedPlugin> _loadedPlugins = [];

    public PluginLoader(IPluginHost host, OutputPaneViewModel output)
    {
        _host = host;
        _output = output;
    }

    /// <summary>
    /// Every plugin instance currently loaded, one entry per constructed <see cref="IIdePlugin"/> (not
    /// yet surfaced in any UI - exposed for a future "installed plugins" view and for
    /// <see cref="TryUnload"/>).
    /// </summary>
    public IReadOnlyList<LoadedPlugin> LoadedPlugins => _loadedPlugins;

    public void LoadPlugins() => LoadPlugins(PluginsDirectory);

    /// <summary>
    /// Overload taking an explicit directory - <see cref="PluginsDirectory"/> is a fixed, static
    /// %LOCALAPPDATA% path, which is exactly what production startup wants but unusable from a test
    /// that needs to point at a scratch directory without touching the real user profile.
    /// </summary>
    public void LoadPlugins(string pluginsDirectory)
    {
        Directory.CreateDirectory(pluginsDirectory);

        foreach (var dllPath in Directory.GetFiles(pluginsDirectory, "*.dll"))
        {
            try
            {
                LoadPluginsFrom(dllPath);
            }
            catch (ReflectionTypeLoadException ex)
            {
                // The single most likely failure from loading an untrusted DLL (e.g. a missing
                // dependency) - ex.Message alone is close to useless without the per-type
                // LoaderExceptions.
                var loaderErrors = string.Join(
                    "; ", ex.LoaderExceptions.Where(le => le is not null).Select(le => le!.Message));
                _output.AppendLine($"[Plugin] Failed to load {Path.GetFileName(dllPath)}: {ex.Message} ({loaderErrors})");
            }
            catch (Exception ex)
            {
                _output.AppendLine($"[Plugin] Failed to load {Path.GetFileName(dllPath)}: {ex}");
            }
        }
    }

    private void LoadPluginsFrom(string dllPath)
    {
        var context = new PluginLoadContext(dllPath);
        var anyTracked = false;

        try
        {
            var assembly = context.LoadFromAssemblyPath(dllPath);

            // ToList() forces enumeration (and so any ReflectionTypeLoadException from GetTypes())
            // inside this try, before the finally below decides whether the context is unused.
            var pluginTypes = assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IIdePlugin).IsAssignableFrom(t))
                .ToList();

            foreach (var type in pluginTypes)
            {
                if (Activator.CreateInstance(type) is not IIdePlugin plugin)
                {
                    continue;
                }

                // Tracked as soon as it exists - even if Initialize (below) throws, PluginLoader
                // still holds a live plugin sitting in a live context, so it must stay tracked to be
                // unloadable later.
                _loadedPlugins.Add(new LoadedPlugin(context, plugin, dllPath));
                anyTracked = true;

                // IPluginHost v2's new Register* methods take no plugin-name parameter of their own
                // (that would mean changing method signatures per call site, not per plugin), so the
                // concrete PluginHost tracks "whose Initialize is running right now" via this ambient
                // setter instead - set immediately before the call and cleared immediately after,
                // regardless of outcome. A no-op for any other IPluginHost (e.g. a test fake), which
                // is exactly what the existing hard-gate/compat tests below exercise.
                if (_host is PluginHost pluginHostForScope)
                {
                    pluginHostForScope.SetCurrentPlugin(plugin.Name);
                }

                try
                {
                    plugin.Initialize(_host);
                    _output.AppendLine($"[Plugin] Loaded: {plugin.Name} ({Path.GetFileName(dllPath)})");
                }
                catch (Exception ex)
                {
                    _output.AppendLine($"[Plugin] {plugin.Name} threw during Initialize: {ex}");
                }
                finally
                {
                    if (_host is PluginHost pluginHostForScope2)
                    {
                        pluginHostForScope2.SetCurrentPlugin(null);
                    }
                }
            }
        }
        finally
        {
            if (!anyTracked)
            {
                // Nothing from this DLL ended up tracked (no IIdePlugin types, none constructible, or
                // an exception propagating out of this method) - don't leak a live collectible ALC
                // that nothing will ever reference again.
                context.Unload();
            }
        }
    }

    /// <summary>
    /// Unloads the plugin's <see cref="PluginLoadContext"/> and drops every reference this class holds
    /// to it (and to any sibling <see cref="LoadedPlugin"/> sharing the same context - see that class's
    /// doc comment). Returns a <see cref="WeakReference"/> to the load context via
    /// <paramref name="contextWeakReference"/> so a caller can verify the context is actually collected
    /// (<c>AssemblyLoadContext.Unload()</c> does not throw or report failure if something else is still
    /// rooting the context - it just silently never collects).
    ///
    /// Deliberately not wired to any UI/menu command yet - "reload plugin" is explicitly deferred. This
    /// exists so unload correctness is real and verifiable now (see the GC/WeakReference test), ahead
    /// of that UI.
    /// </summary>
    public bool TryUnload(LoadedPlugin loadedPlugin, out WeakReference contextWeakReference)
    {
        contextWeakReference = loadedPlugin.ContextWeakReference;

        var context = loadedPlugin.LoadContext;
        if (context is null)
        {
            // Already unloaded.
            return false;
        }

        var siblings = _loadedPlugins.Where(lp => ReferenceEquals(lp.LoadContext, context)).ToList();
        _loadedPlugins.RemoveAll(siblings.Contains);

        foreach (var sibling in siblings)
        {
            sibling.ReleaseReferences();
        }

        context.Unload();
        return true;
    }
}
