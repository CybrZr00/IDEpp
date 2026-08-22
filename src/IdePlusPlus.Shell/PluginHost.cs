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
        _mainWindowViewModel.AddPluginCommand(header, execute);

    public void LogInfo(string message) => _output.AppendLine($"[Plugin] {message}");
}
