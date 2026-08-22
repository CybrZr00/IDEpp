namespace IdePlusPlus.PluginContracts;

/// <summary>
/// Implement this in a plugin assembly and drop the built DLL (plus any files it needs alongside it,
/// e.g. a .xshd) into %LOCALAPPDATA%\IdePlusPlus\plugins. IDE++ discovers every IIdePlugin
/// implementation there at startup via reflection (no MEF/composition catalog - see PluginLoader)
/// and calls Initialize once. A plugin that throws during discovery or Initialize is skipped and
/// logged to the Output pane; it never prevents the rest of the IDE from starting.
/// </summary>
public interface IIdePlugin
{
    string Name { get; }

    void Initialize(IPluginHost host);
}
