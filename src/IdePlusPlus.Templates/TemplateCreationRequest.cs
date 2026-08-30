namespace IdePlusPlus.Templates;

/// <summary>
/// What to create. <paramref name="OutputDirectory"/> is where the template's own output goes -
/// for a project template the CLI creates it if absent, so it is the project directory rather than
/// its parent.
/// </summary>
/// <param name="Parameters">Template-specific parameters by name, without the leading dashes. A null
/// value means a switch (<c>--actions</c>), not a missing one.</param>
public sealed record TemplateCreationRequest(
    string ShortName,
    string Name,
    string OutputDirectory,
    string? Language = null,
    string? Framework = null,
    string? Namespace = null,
    IReadOnlyDictionary<string, string?>? Parameters = null)
{
    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        Parameters ?? new Dictionary<string, string?>();
}

/// <param name="CreatedFiles">Full paths of the files the template produced, build output excluded.
/// Empty on failure, and also for a template that genuinely wrote nothing.</param>
/// <param name="ExitCode">The CLI's own exit code. 73 is a refusal to overwrite - which is not a
/// crash, and is easy to read as success if only stdout is looked at.</param>
public sealed record TemplateCreationResult(int ExitCode, IReadOnlyList<string> CreatedFiles)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The exit code <c>dotnet new</c> uses when creating would overwrite existing files.</summary>
    public const int DestructiveChangesExitCode = 73;

    public bool WouldOverwriteExistingFiles => ExitCode == DestructiveChangesExitCode;
}
