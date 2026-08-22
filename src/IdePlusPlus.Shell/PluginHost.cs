using IdePlusPlus.Editor;
using IdePlusPlus.PluginContracts;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

public sealed class PluginHost : IPluginHost
{
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly OutputPaneViewModel _output;

    public PluginHost(MainWindowViewModel mainWindowViewModel, OutputPaneViewModel output)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _output = output;
    }

    public void RegisterHighlighting(string fileExtension, string xshdFilePath) =>
        HighlightingRegistry.RegisterCustom(fileExtension, xshdFilePath);

    public void RegisterToolsMenuCommand(string header, Action execute) =>
        _mainWindowViewModel.AddPluginCommand(header, () => RunPluginCommand(header, execute));

    public void LogInfo(string message) => _output.AppendLine($"[Plugin] {message}");

    // PluginLoader guards a plugin's Initialize() call, but nothing previously guarded a command
    // registered via RegisterToolsMenuCommand once wired into the menu - a throw from here reached
    // the global DispatcherUnhandledException handler with no plugin-specific context.
    private void RunPluginCommand(string header, Action execute)
    {
        try
        {
            execute();
        }
        catch (Exception ex)
        {
            _output.AppendLine($"[Plugin] Command '{header}' threw: {ex}");
        }
    }
}
