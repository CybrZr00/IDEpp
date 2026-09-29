namespace IdePlusPlus.Templates;

/// <summary>The outcome of an install or uninstall, with whatever the CLI printed.</summary>
public sealed record TemplatePackageResult(bool Succeeded, string Output);

/// <summary>
/// Lists, installs and uninstalls template packages.
///
/// <para>Not routed through <c>BuildService</c>'s guard, unlike creating a template: installing a
/// package downloads a .nupkg into the user profile and rewrites the template cache, touching no
/// project's obj/bin. Blocking it behind "a build is already running" would be a rule with no reason
/// behind it.</para>
///
/// <para>Deliberately has no copilot tool. A template package is arbitrary code that later runs as a
/// template on the user's machine, fetched by name from a public feed - installing one is a decision
/// with the same shape as the shell-command and MCP-trust prompts elsewhere in this codebase, and it
/// stays user-initiated from a dialog.</para>
/// </summary>
public sealed class TemplatePackageService(TemplateCatalog catalog)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly TemplateCatalog _catalog = catalog;

    /// <summary>What is installed right now, newest-looking first (the engine writes them in install
    /// order, which is close enough to be useful and is not worth re-sorting against).</summary>
    public IReadOnlyList<TemplatePackage> Installed() =>
        TemplatePackageReader.ReadFile(TemplatePackageReader.PathFor(TemplateCacheLocator.CliHome));

    /// <summary>
    /// <c>dotnet new install &lt;package&gt;</c>. The identifier is passed verbatim - it may be a NuGet
    /// package id, an id::version, or a path to a folder or .nupkg, all of which the CLI accepts and
    /// none of which this needs to understand.
    /// </summary>
    public Task<TemplatePackageResult> InstallAsync(
        string package, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        RunAsync(["new", "install", package], workingDirectory, cancellationToken);

    public Task<TemplatePackageResult> UninstallAsync(
        string package, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        RunAsync(["new", "uninstall", package], workingDirectory, cancellationToken);

    private async Task<TemplatePackageResult> RunAsync(
        IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var result = await DotnetCliRunner.RunAsync(
            arguments, workingDirectory ?? Environment.CurrentDirectory, CommandTimeout, cancellationToken);

        if (result is null)
        {
            return new TemplatePackageResult(false, "Couldn't run dotnet - is the .NET SDK installed and on PATH?");
        }

        // Either way the cache on disk may have been rewritten, and the memoized catalog is now
        // describing templates that have moved. Cheaper to always invalidate than to work out
        // whether it mattered.
        _catalog.Invalidate();

        return new TemplatePackageResult(result.ExitCode == 0, result.Output);
    }
}
