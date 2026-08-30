namespace IdePlusPlus.Tasks;

/// <summary>
/// One runnable task. <paramref name="ManifestPath"/> is the full path of the file it came from -
/// its directory is the working directory the task runs in, so there is no separate
/// WorkingDirectory field to keep in sync. <paramref name="Command"/> is the raw script text
/// (package.json's <c>scripts</c> value), kept for display only: it is never executed as a string,
/// since the task is run as <c>&lt;package manager&gt; run &lt;name&gt;</c>.
/// </summary>
public sealed record TaskDefinition(
    TaskManifestKind ManifestKind,
    string ManifestPath,
    string Name,
    string? Command);
