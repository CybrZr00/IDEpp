using System.Runtime.CompilerServices;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The hard gate from docs/bigger-bets-designs.md section 5's Tier 1: proves a plugin's collectible
/// PluginLoadContext is actually collected after PluginLoader.TryUnload, not merely that TryUnload runs
/// without throwing. That distinction matters because AssemblyLoadContext.Unload() famously never
/// throws or reports failure - if anything still roots the context it just silently never collects, so
/// asserting "TryUnload returned true" alone would prove nothing.
///
/// Framing reminder (see PluginLoadContext's doc comment for the full version): this test proves Tier
/// 1 unload/versioning works. It says nothing about sandboxing - a plugin loaded this way can still
/// reach every other assembly in the process via AppDomain.CurrentDomain.GetAssemblies(),
/// Activator.CreateInstance any host type, P/Invoke, read arbitrary files, or call Environment.Exit.
///
/// The scratch plugin is a real assembly built by a real, independent `dotnet build` (see
/// ScratchPluginProject) - not a hand-rolled Reflection.Emit stand-in - specifically so this test also
/// exercises the thing most likely to silently break a naive ALC plugin loader: whether the plugin's
/// own compile-time reference to IdePlusPlus.PluginContracts ends up resolving, at load time, to the
/// SAME runtime IIdePlugin/IPluginHost types the host uses. If PluginLoadContext.Load ever stopped
/// forwarding that assembly to the Default ALC, `Activator.CreateInstance(type) is IIdePlugin` inside
/// PluginLoader would start silently returning false for every plugin, and this test would fail at the
/// `LoadedPlugins.Count.ShouldBe(1)` assertion below - not with some obscure loader exception.
/// </summary>
public sealed class PluginLoaderUnloadTests
{
    private const string PluginSource = """
        using IdePlusPlus.PluginContracts;

        namespace ScratchPlugin;

        public sealed class ScratchIdePlugin : IIdePlugin
        {
            public string Name => "Scratch Test Plugin";

            public void Initialize(IPluginHost host)
            {
                host.LogInfo("scratch plugin initialized");
            }
        }
        """;

    [Fact]
    public void TryUnload_PluginContextIsEventuallyCollected()
    {
        using var scratchProject = new ScratchPluginProject("ScratchPlugin", PluginSource);
        var dllPath = scratchProject.Build();

        var pluginsDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pluginsDirectory);
        File.Copy(dllPath, Path.Combine(pluginsDirectory, Path.GetFileName(dllPath)));

        try
        {
            var host = new RecordingPluginHost();
            var loader = new PluginLoader(host, new OutputPaneViewModel());

            var contextWeakReference = LoadInitializeAndUnload(loader, pluginsDirectory, host);

            var retries = 0;
            const int maxRetries = 10;
            while (contextWeakReference.IsAlive && retries < maxRetries)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(50);
                retries++;
            }

            contextWeakReference.IsAlive.ShouldBeFalse(
                $"the plugin's PluginLoadContext was still alive after {retries} GC-and-wait cycles following TryUnload.");
        }
        finally
        {
            Directory.Delete(pluginsDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Isolated into its own non-inlined method so none of its locals (the loaded assembly, the plugin
    /// instance, the LoadedPlugin wrapper) can still be reachable from the calling test method's stack
    /// frame once this returns - the standard pattern for collectible-ALC unload-verification tests,
    /// since a lingering local can keep a "collected" context alive until the enclosing method itself
    /// returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadInitializeAndUnload(PluginLoader loader, string pluginsDirectory, RecordingPluginHost host)
    {
        loader.LoadPlugins(pluginsDirectory);

        loader.LoadedPlugins.Count.ShouldBe(1);
        host.LoggedMessages.ShouldContain(m => m.Contains("scratch plugin initialized"));

        var loadedPlugin = loader.LoadedPlugins[0];
        var unloaded = loader.TryUnload(loadedPlugin, out var contextWeakReference);

        unloaded.ShouldBeTrue();
        loader.LoadedPlugins.ShouldBeEmpty();
        loadedPlugin.Plugin.ShouldBeNull();
        loadedPlugin.LoadContext.ShouldBeNull();

        return contextWeakReference;
    }
}
