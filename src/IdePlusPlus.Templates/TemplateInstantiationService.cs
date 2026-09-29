using IdePlusPlus.Build;

namespace IdePlusPlus.Templates;

/// <summary>
/// Creates a template, and adds a created project to a solution.
///
/// <para>Both go through <c>BuildService.RunDotnetAsync</c> rather than spawning directly, for the
/// same reason <c>PublishService</c> does: a project template's restore post-action writes the same
/// obj/bin a build would, and <c>dotnet solution add</c> rewrites the file the solution is loaded
/// from. Sharing one guard also means "a build is already running" is one fact rather than several
/// that have to agree.</para>
/// </summary>
public sealed class TemplateInstantiationService(BuildService buildService)
{
    private readonly BuildService _buildService = buildService;

    public bool IsRunning => _buildService.IsRunning;

    /// <summary>
    /// Runs one template and reports what it wrote. A non-zero exit is a failure even though
    /// <c>dotnet new</c> prints prose and exits 73 rather than crashing when it would overwrite
    /// something - see <see cref="TemplateCreationResult.WouldOverwriteExistingFiles"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The template can't be created by name - see
    /// <see cref="TemplateCreationArguments.ForCreate"/>.</exception>
    /// <exception cref="InvalidOperationException">A build, publish or create is already in flight.</exception>
    public async Task<TemplateCreationResult> CreateAsync(
        TemplateCreationRequest request,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
    {
        var arguments = TemplateCreationArguments.ForCreate(request);

        // Taken before the run, and against the output directory itself, so a project template that
        // creates that directory from nothing still reports every file in it.
        var before = CreatedFileScanner.Snapshot(request.OutputDirectory);

        // The CLI resolves --output relative to the working directory; anchoring it at the output
        // directory's parent keeps a relative path in the request meaningful rather than depending on
        // wherever the IDE process happens to be.
        //
        // Walked up to something that exists, because Process.Start fails outright on a working
        // directory that does not - and `dotnet new` is perfectly happy to create a nested --output
        // whose parents are absent. Adding a class to a folder that doesn't exist yet used to fail
        // with "Could not run dotnet - is the SDK installed and on PATH?", which is both wrong and
        // impossible to act on.
        var workingDirectory = NearestExistingDirectory(
            Path.GetDirectoryName(Path.GetFullPath(request.OutputDirectory)) ?? request.OutputDirectory);

        var exitCode = await _buildService.RunDotnetAsync(
            arguments, workingDirectory, onOutputLine, cancellationToken);

        return new TemplateCreationResult(
            exitCode,
            exitCode == 0 ? CreatedFileScanner.NewFilesSince(request.OutputDirectory, before) : []);
    }

    /// <summary>The nearest ancestor of <paramref name="path"/> that exists on disk, for use as a
    /// working directory. Falls back to the process's own directory, which always does.</summary>
    public static string NearestExistingDirectory(string path)
    {
        var current = Path.GetFullPath(path);

        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
            {
                return Environment.CurrentDirectory;
            }

            current = parent;
        }

        return current;
    }

    /// <summary>
    /// Creates one item and makes sure it ends up in the right namespace - passing
    /// <c>--namespace</c> where the template takes one, and rewriting what was emitted where it does
    /// not. See <see cref="ItemCreationPlan"/> for which templates fall on which side.
    ///
    /// <para>A failed correction is not a failed creation: the file exists and is usable, it just has
    /// the namespace `dotnet new` chose, so it is reported rather than thrown.</para>
    /// </summary>
    public async Task<TemplateCreationResult> CreateItemAsync(
        ItemCreationPlan plan,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateAsync(plan.Request, onOutputLine, cancellationToken);

        if (!result.Succeeded || !plan.CorrectAfterCreation)
        {
            return result;
        }

        var corrected = result.CreatedFiles
            .Count(file => NamespaceCorrector.TryCorrect(file, plan.BoundNamespace, plan.DesiredNamespace));

        if (corrected == 0)
        {
            onOutputLine(
                $"Created the file, but couldn't set its namespace to {plan.DesiredNamespace} - " +
                $"it will have the project's root namespace ({plan.BoundNamespace}) instead.");
        }

        return result;
    }

    /// <summary>
    /// Adds a project to a solution. Separate from <see cref="CreateAsync"/> because a solution-kind
    /// template brings its own solution file and must not be added to another one.
    /// </summary>
    public Task<int> AddToSolutionAsync(
        string solutionPath,
        string projectPath,
        string? solutionFolder,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default) =>
        _buildService.RunDotnetAsync(
            TemplateCreationArguments.ForSolutionAdd(solutionPath, projectPath, solutionFolder),
            Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory,
            onOutputLine,
            cancellationToken);

    /// <summary>
    /// Creates a project and adds it to a solution as one guarded operation, so nothing can start a
    /// build in the gap between a project existing on disk and the solution knowing about it.
    /// Returns the create result; the add's own failure is reported through
    /// <paramref name="onOutputLine"/> rather than replacing it, since a created-but-unadded project
    /// is a recoverable state worth telling the user about accurately.
    /// </summary>
    public Task<TemplateCreationResult> CreateAndAddToSolutionAsync(
        TemplateCreationRequest request,
        string solutionPath,
        string? solutionFolder,
        string projectPath,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default) =>
        _buildService.RunExclusiveAsync(async () =>
        {
            var created = await CreateAsync(request, onOutputLine, cancellationToken);
            if (!created.Succeeded)
            {
                return created;
            }

            var addExitCode = await AddToSolutionAsync(
                solutionPath, projectPath, solutionFolder, onOutputLine, cancellationToken);

            if (addExitCode != 0)
            {
                onOutputLine(
                    $"The project was created at {projectPath} but couldn't be added to the solution. " +
                    "Add it manually, or reopen the solution after fixing the error above.");
            }

            return created;
        });
}
