namespace IdePlusPlus.ProjectSystem;

/// <summary>What Run/Debug should do with a project in a <see cref="StartupConfiguration"/>, mirroring
/// the per-project Action column in Visual Studio's "Set Startup Projects" dialog.</summary>
public enum StartupAction
{
    None,
    Start,
    StartWithoutDebugging,
}

/// <summary>One project's role within a <see cref="StartupConfiguration"/>: whether/how it launches,
/// and which launchSettings.json profile (by name; null means "use the default profile resolution")
/// it launches with.</summary>
public sealed record StartupConfigurationEntry(string ProjectPath, StartupAction Action, string? LaunchProfileName);

/// <summary>
/// A named, saved set of startup projects with per-project action/profile/order - the unit the Startup
/// Configuration Manager dialog edits and switches between (analogous to VS Code's compound launch
/// configs, layered on top of Visual Studio's single "Set Startup Projects" selection).
/// </summary>
public sealed record StartupConfiguration(string Name, IReadOnlyList<StartupConfigurationEntry> Entries);
