using IdePlusPlus.PluginContracts;

namespace AccessVbaPlugin;

/// <summary>
/// Phase 1 of the VBA/Access support plan (see docs/vba-access-support-scope.md): a tool window for
/// pulling a VBA module's code out of an Access application, editing it, and pushing it back - via
/// AccessMCP.Core in-process, not files, for this first cut. No core IDE++ changes; everything here
/// runs through IPluginHost.RegisterToolWindow.
/// </summary>
public sealed class AccessVbaPlugin : IIdePlugin
{
    public string Name => "Access VBA";

    public void Initialize(IPluginHost host)
    {
        host.RegisterToolWindow("Access VBA", () => new AccessVbaToolWindow(host));
        host.LogInfo($"{Name} initialized - registered the Access VBA tool window.");
    }
}
