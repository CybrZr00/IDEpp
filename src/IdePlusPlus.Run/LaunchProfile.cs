namespace IdePlusPlus.Run;

public sealed record LaunchProfile(
    string Name,
    string CommandName,
    bool LaunchBrowser,
    string? ApplicationUrl,
    IReadOnlyList<string> CommandLineArgs,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
