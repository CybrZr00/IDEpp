using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace IdePlusPlus.Shell;

/// <summary>
/// A collectible, per-plugin-DLL <see cref="AssemblyLoadContext"/>. This buys two things and two
/// things only: the ability to actually unload a plugin's assemblies later (Tier 1 - clean
/// unload/versioning), and a narrower blast radius for an ACCIDENTAL type clash between two plugins
/// or between a plugin and the host (Tier 2 - hardening against accidents). It is explicitly NOT a
/// security boundary against a deliberately malicious plugin: a collectible ALC does not hide other
/// loaded assemblies from reflection, so code running inside this context can still call
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c>, <c>Activator.CreateInstance(typeof(SecureSettingsStore))</c>,
/// P/Invoke into arbitrary native code, read/write any file the host process can reach, or call
/// <c>Environment.Exit</c> - none of that is affected by which ALC the plugin's own assembly happens
/// to be loaded into. A genuine security boundary would require an out-of-process host (deferred to a
/// future Tier 3 - see docs/bigger-bets-designs.md section 5).
///
/// Private plugin dependencies (extra DLLs sitting next to the plugin's own assembly) are resolved via
/// <see cref="AssemblyDependencyResolver"/> rooted at the plugin DLL's own directory - the standard
/// .NET "plugin with AssemblyLoadContext" pattern. A fixed set of shared/host assembly names is
/// deliberately carved out of that resolution: <see cref="Load"/> returns null for any of them so the
/// runtime falls through to the Default ALC instead. This is the single most important detail in this
/// class - without it, a plugin that (say) merely ships its own copy of IdePlusPlus.PluginContracts.dll
/// next to itself would get a SECOND, distinct runtime type for <c>IIdePlugin</c>/<c>IPluginHost</c>,
/// and every `is`/`as`/interface dispatch across the host/plugin boundary would fail with confusing
/// "type X is not assignable to type X" errors even though the two types have the same fully-qualified
/// name. Forwarding WPF/AvalonEdit/AvalonDock/System.Text.Json the same way avoids the same problem
/// for any type from those assemblies a plugin's code touches (e.g. an XSHD-driven highlighting
/// definition, or - once IPluginHost v2 lands - a WPF UserControl handed back to the host).
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assembly simple names that must resolve to the Default ALC's already-loaded copy rather than a
    /// second copy loaded into this context. Kept as simple names (no version/culture/public key) since
    /// that's all <see cref="AssemblyName.Name"/> reliably gives us and it's sufficient for this list -
    /// these are all first-party or well-known third-party assemblies the host itself loads exactly
    /// once.
    /// </summary>
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "IdePlusPlus.PluginContracts",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "ICSharpCode.AvalonEdit", // package "avalonedit" - this is the actual assembly name.
        "AvalonDock",             // package "Dirkster.AvalonDock" - this is the actual assembly name.
        "System.Text.Json",
    };

    private readonly AssemblyDependencyResolver _resolver;

    /// <param name="pluginDllPath">Full path to the plugin's own DLL. Used both as the
    /// AssemblyDependencyResolver root and as this context's diagnostic name (visible in a debugger's
    /// module list and in dotnet-dump/dotnet-trace output as "AssemblyLoadContext: &lt;name&gt;") so a
    /// leaked context can actually be identified later.</param>
    public PluginLoadContext(string pluginDllPath)
        : base(name: Path.GetFileName(pluginDllPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginDllPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            // Returning null here tells the runtime to keep looking - it falls through to the
            // Default ALC, which is exactly where the host's own copy already lives.
            return null;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}
