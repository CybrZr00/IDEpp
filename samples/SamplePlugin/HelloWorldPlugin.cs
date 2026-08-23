using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using IdePlusPlus.PluginContracts;

namespace SamplePlugin;

/// <summary>
/// Demonstrates both IPluginHost v1 extension points (custom syntax highlighting for INI files,
/// which IDE++ doesn't otherwise support - see Ini.xshd - and a Tools > Plugins menu command), plus
/// two of v2's additive capabilities (a Solution Explorer context-menu command and a chat tool). The
/// v1 registrations above the "v2 capabilities" comment below are deliberately left exactly as they
/// were before v2 existed - samples/SamplePlugin is this repo's concrete backward-compat check
/// (see tests/IdePlusPlus.Shell.Tests/SamplePluginCompatibilityTests.cs), so the v1 behavior it
/// exercises must keep working unchanged even as the plugin picks up new capabilities.
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

        // v2 capabilities - additive, exercised for real rather than just declared.
        host.RegisterSolutionExplorerContextMenuCommand(
            "Log Path (Sample Plugin)",
            canExecute: _ => true,
            execute: context => host.LogInfo($"Solution Explorer context menu invoked for '{context.Path}' (IsDirectory={context.IsDirectory})."));

        var greetToolSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Who to greet." },
            },
        };

        host.RegisterChatTool(
            new PluginToolDefinition(
                Name: "greet",
                Description: "Returns a friendly greeting for the given name - a trivial demonstration chat tool.",
                ParametersSchema: greetToolSchema),
            execute: (arguments, _) =>
            {
                var name = arguments?["name"]?.GetValue<string>() ?? "there";
                return Task.FromResult(new PluginToolResult(IsError: false, Text: $"Hello, {name}, from the Sample Plugin's chat tool!"));
            });

        host.LogInfo($"{Name} also registered a Solution Explorer context menu command and a 'greet' chat tool (v2 capabilities).");
    }
}
