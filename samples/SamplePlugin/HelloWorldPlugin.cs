using System.IO;
using System.Reflection;
using System.Windows;
using IdePlusPlus.PluginContracts;

namespace SamplePlugin;

/// <summary>
/// Demonstrates both IPluginHost extension points: custom syntax highlighting (INI files, which
/// IDE++ doesn't otherwise support - see Ini.xshd) and a Tools > Plugins menu command.
/// </summary>
public sealed class HelloWorldPlugin : IIdePlugin
{
    public string Name => "Hello World Sample Plugin";

    public void Initialize(IPluginHost host)
    {
        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        host.RegisterHighlighting(".ini", Path.Combine(pluginDirectory, "Ini.xshd"));

        host.RegisterToolsMenuCommand("Say Hello (Sample Plugin)", () =>
            MessageBox.Show("Hello from a plugin!", "Sample Plugin"));

        host.LogInfo($"{Name} initialized - registered .ini highlighting and a Tools menu command.");
    }
}
