using AccessMCP;
using IdePlusPlus.PluginContracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AccessVbaPlugin;

/// <summary>
/// The VBA/Access support plan's plugin (see docs/vba-access-support-scope.md): a tool window for
/// pulling a VBA module's code out of an Access application, editing it, and pushing it back - via
/// AccessMCP.Core in-process, not files - plus three chat tools (<see cref="VbaChatTools"/>) so the
/// Copilot can read that VBA too, since neither "Include current file" nor its solution-scoped file
/// tools can reach code that lives inside Access. No core IDE++ changes; everything here runs through
/// IPluginHost.RegisterToolWindow and IPluginHost.RegisterChatTool.
/// </summary>
public sealed class AccessVbaPlugin : IIdePlugin
{
    public string Name => "Access VBA";

    public void Initialize(IPluginHost host)
    {
        // The host calls this factory exactly once, after Initialize has returned (see
        // IPluginHost.RegisterToolWindow), so the chat tools reach the window through this local -
        // null until then - rather than constructing a WPF control here on the loader's thread.
        AccessVbaToolWindow? window = null;
        host.RegisterToolWindow("Access VBA", () => window = new AccessVbaToolWindow(host));

        // Same config file and the same in-process AccessTools calls the window itself uses, so the
        // chat reads the database the user is actually editing.
        var tools = new AccessTools(AccessVbaToolWindow.LoadOptions(), NullLoggerFactory.Instance);
        var chatTools = new VbaChatTools(
            getCurrentModule: async _ => window is null ? null : await window.GetCurrentModuleSnapshotAsync(),
            listModulesJson: cancellationToken => tools.ListVbeModulesAsync(cancellationToken: cancellationToken),
            getModuleJson: (moduleName, cancellationToken) => tools.GetVbeModuleCodeAsync(moduleName, cancellationToken: cancellationToken));
        chatTools.RegisterAll(host);

        host.LogInfo($"{Name} initialized - registered the Access VBA tool window and the " +
                     "vba_get_current_module, vba_list_modules and vba_get_module chat tools.");
    }
}
