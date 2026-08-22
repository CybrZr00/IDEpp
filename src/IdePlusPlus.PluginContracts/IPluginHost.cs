namespace IdePlusPlus.PluginContracts;

/// <summary>
/// The IDE's side of the plugin contract, passed to IIdePlugin.Initialize. Deliberately small - each
/// method is a narrow, additive capability rather than exposing IDE++'s internals wholesale, so a
/// plugin can't reach into (or break) state it doesn't own.
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// Registers syntax highlighting for a file extension (e.g. ".ini") from a .xshd file. Takes
    /// priority over IDE++'s own built-in mapping for that extension if there is one, so a plugin
    /// can also override the built-in highlighting for a language it wants to handle differently.
    /// </summary>
    void RegisterHighlighting(string fileExtension, string xshdFilePath);

    /// <summary>Adds a command under Tools > Plugins.</summary>
    void RegisterToolsMenuCommand(string header, Action execute);

    /// <summary>Writes a line to the Output pane, prefixed to identify it as coming from a plugin.</summary>
    void LogInfo(string message);
}
