namespace IdePlusPlus.Run;

public sealed record LaunchProfile(
    string Name,
    bool LaunchBrowser,
    string? ApplicationUrl,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
