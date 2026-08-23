using IdePlusPlus.PluginContracts;

namespace IdePlusPlus.Shell;

/// <summary>
/// Everything <see cref="PluginLoader"/> tracks about one successfully-constructed <see cref="IIdePlugin"/>
/// instance: the collectible <see cref="PluginLoadContext"/> it was loaded into, the live plugin
/// instance itself, the DLL path it came from, and a <see cref="WeakReference"/> to the LOAD CONTEXT
/// (deliberately not to the plugin instance - the context is what actually holds every assembly, every
/// type, and every static field the plugin allocated, so it's the right thing to watch for collection).
///
/// A <see cref="PluginLoadContext"/> currently maps one-to-one with a <see cref="LoadedPlugin"/> under
/// the assumption that a plugin DLL contains exactly one <see cref="IIdePlugin"/> implementation (true
/// for every known/sample plugin). If a single DLL ever registered more than one, they would share the
/// same <see cref="LoadContext"/> instance - unloading any one of the corresponding
/// <see cref="LoadedPlugin"/> entries unloads that whole context, since AssemblyLoadContext has no
/// concept of partially unloading itself. <see cref="PluginLoader"/> accounts for this by removing every
/// tracked entry that shares a context whenever one of them is unloaded.
///
/// Not currently surfaced in any UI (no "installed plugins" view exists yet) and unloading is not wired
/// to any menu command ("reload plugin" is explicitly deferred) - this exists purely so unload
/// correctness is real and testable now, ahead of that UI.
/// </summary>
public sealed class LoadedPlugin
{
    public LoadedPlugin(PluginLoadContext loadContext, IIdePlugin plugin, string dllPath)
    {
        LoadContext = loadContext;
        Plugin = plugin;
        DllPath = dllPath;
        ContextWeakReference = new WeakReference(loadContext);
    }

    /// <summary>The plugin's own collectible load context. Null after <see cref="ReleaseReferences"/>
    /// has run (i.e. after the owning <see cref="PluginLoader"/> has unloaded it) - nulled out
    /// specifically so this object doesn't itself keep the context reachable if something outside
    /// PluginLoader is still holding onto this LoadedPlugin instance.</summary>
    public PluginLoadContext? LoadContext { get; private set; }

    /// <summary>The live plugin instance. Null after <see cref="ReleaseReferences"/> for the same
    /// reason as <see cref="LoadContext"/>.</summary>
    public IIdePlugin? Plugin { get; private set; }

    public string DllPath { get; }

    /// <summary>
    /// A WeakReference to <see cref="LoadContext"/> (not the plugin instance) captured at construction
    /// time, so it stays valid for verification even after <see cref="ReleaseReferences"/> has nulled
    /// out the strong reference above. The standard collectible-ALC unload-verification pattern is:
    /// unload the context, drop every strong reference to it, force a GC, then check
    /// <c>ContextWeakReference.IsAlive</c>.
    /// </summary>
    public WeakReference ContextWeakReference { get; }

    /// <summary>
    /// Called by <see cref="PluginLoader.TryUnload"/> once it has called <c>LoadContext.Unload()</c>
    /// and removed this instance (and any sibling instances sharing the same context) from its own
    /// tracked list. Nulling these out here - rather than only removing the list entry - means a
    /// caller that kept its own reference to this LoadedPlugin object (e.g. a future "installed
    /// plugins" view) can't accidentally keep the whole context graph alive just by holding this
    /// wrapper.
    /// </summary>
    internal void ReleaseReferences()
    {
        LoadContext = null;
        Plugin = null;
    }
}
