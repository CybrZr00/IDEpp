namespace IdePlusPlus.PluginContracts;

/// <summary>
/// What a build-event handler is told about the build it is running alongside.
///
/// <paramref name="ExitCode"/> is null for the Before* events, where the build has not run yet, and
/// set for the After* ones. <paramref name="Kind"/> is included so one handler method can be
/// registered for several events and still tell them apart.
/// </summary>
public sealed record BuildEventContext(
    BuildEventKind Kind,
    string SolutionPath,
    string Configuration,
    int? ExitCode);
