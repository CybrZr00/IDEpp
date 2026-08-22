using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace IdePlusPlus.Shell;

/// <summary>A single Tools > Plugins menu entry a plugin registered via IPluginHost.RegisterToolsMenuCommand.</summary>
public sealed class PluginMenuCommand
{
    public PluginMenuCommand(string header, Action execute)
    {
        Header = header;
        Command = new RelayCommand(execute);
    }

    public string Header { get; }

    public ICommand Command { get; }
}
