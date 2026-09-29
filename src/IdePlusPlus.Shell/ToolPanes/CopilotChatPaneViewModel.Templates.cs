using System.IO;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Templates;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// The three template tools' handlers. In their own file because the chat pane is already the
/// largest class here and these share nothing with the rest of it beyond the solution context.
/// </summary>
public sealed partial class CopilotChatPaneViewModel
{
    /// <summary>
    /// Read-only. Leads with the short name because that is the only part the other two tools take,
    /// and guessing it is the failure this tool exists to prevent.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunListTemplatesAsync(
        string? kind, string? search, CancellationToken cancellationToken)
    {
        var wanted = kind?.ToLowerInvariant() switch
        {
            "project" => TemplateKind.Project,
            "item" => TemplateKind.Item,
            _ => (TemplateKind?)null,
        };

        var groups = await _templateCatalog.GetGroupsAsync(wanted, _solutionDirectory, cancellationToken);
        var matches = TemplateGrouping.Search(groups, search);

        if (matches.Count == 0)
        {
            return (false, groups.Count == 0
                ? "No templates found. The .NET SDK may not be installed or on PATH."
                : $"No templates matched '{search}'.");
        }

        var lines = matches.Select(group =>
        {
            var frameworks = group.FrameworkChoicesFor(group.Languages.FirstOrDefault());
            var suffix = frameworks.Count > 0 ? $"  [{string.Join(", ", frameworks)}]" : string.Empty;
            return $"{group.ShortName}  ({group.Kind.ToString().ToLowerInvariant()})  {group.Name}{suffix}";
        });

        return (false, string.Join(Environment.NewLine, lines));
    }

    private async Task<(bool IsError, string Text)> RunCreateProjectAsync(
        string? templateShortName,
        string? name,
        string? framework,
        string? solutionFolder,
        CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null || _solutionPath is null)
        {
            return (true, "No solution is open, so there is nowhere to add a project.");
        }

        if (string.IsNullOrWhiteSpace(templateShortName) || string.IsNullOrWhiteSpace(name))
        {
            return (true, "Both 'template' and 'name' are required.");
        }

        if (TemplateNameValidator.ValidateProjectName(name) is { } nameError)
        {
            return (true, nameError);
        }

        var (template, templateError) =
            await ResolveTemplateAsync(templateShortName, TemplateKind.Project, cancellationToken);
        if (template is null)
        {
            return (true, templateError!);
        }

        if (_buildService.IsRunning)
        {
            return (true, "A build is already running - wait for it to finish before creating a project.");
        }

        var directory = Path.Combine(_solutionDirectory, name);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return (true, $"'{name}' already exists in the solution directory and is not empty.");
        }

        var outputLines = new List<string>();
        var result = await _templateInstantiation.CreateAndAddToSolutionAsync(
            new TemplateCreationRequest(
                template.ShortName,
                name,
                directory,
                Framework: string.IsNullOrWhiteSpace(framework) ? null : framework),
            _solutionPath,
            string.IsNullOrWhiteSpace(solutionFolder) ? null : solutionFolder,
            Path.Combine(directory, $"{name}.csproj"),
            line => { lock (outputLines) { outputLines.Add(line); } },
            cancellationToken);

        if (!result.Succeeded)
        {
            return (true,
                $"Creating {name} failed (exit code {result.ExitCode}).{Environment.NewLine}{Tail(outputLines)}");
        }

        // Reloading the solution is the shell's job, not this pane's - and saying so plainly is more
        // useful to the model than silence, since the project genuinely is not in the tree yet.
        return (false,
            $"Created {name} from '{template.ShortName}' at {directory} and added it to the solution. " +
            $"Files: {string.Join(", ", result.CreatedFiles.Select(Path.GetFileName))}. " +
            "The user should reopen the solution for it to appear in Solution Explorer.");
    }

    private async Task<(bool IsError, string Text)> RunAddItemAsync(
        string? templateShortName,
        string? name,
        string? folder,
        string? project,
        CancellationToken cancellationToken)
    {
        if (_solutionDirectory is null)
        {
            return (true, "No solution is open, so there is nowhere to add an item.");
        }

        if (string.IsNullOrWhiteSpace(templateShortName) || string.IsNullOrWhiteSpace(name))
        {
            return (true, "Both 'template' and 'name' are required.");
        }

        var (projectPath, _, projectError) = ResolvePublishProject(project);
        if (projectError is not null)
        {
            return (true, projectError);
        }

        var (template, templateError) =
            await ResolveTemplateAsync(templateShortName, TemplateKind.Item, cancellationToken);
        if (template is null)
        {
            return (true, templateError!);
        }

        var invalidName = template.NameBecomesTypeName
            ? TemplateNameValidator.ValidateTypeName(name)
            : TemplateNameValidator.ValidateFileName(name);
        if (invalidName is not null)
        {
            return (true, invalidName);
        }

        var projectDirectory = Path.GetDirectoryName(projectPath!)!;

        // Contained against the project rather than the solution: an item belongs to a project, and a
        // path that escapes it produces a file the project's globs never pick up.
        var directory = string.IsNullOrWhiteSpace(folder)
            ? projectDirectory
            : WorkspaceFileReader.ResolveWithinRoot(projectDirectory, folder);

        if (directory is null)
        {
            return (true, $"Refused: '{folder}' resolves outside {Path.GetFileName(projectPath)}.");
        }

        if (_buildService.IsRunning)
        {
            return (true, "A build is already running - wait for it to finish before adding an item.");
        }

        var known = _solutionProjects.FirstOrDefault(
            p => string.Equals(p.Path, projectPath, StringComparison.OrdinalIgnoreCase));

        var plan = ItemCreationPlan.For(
            template,
            name,
            directory,
            projectDirectory,
            known.RootNamespace ?? Path.GetFileNameWithoutExtension(projectPath!),
            projectPath: projectPath);

        var outputLines = new List<string>();
        var result = await _templateInstantiation.CreateItemAsync(
            plan, line => { lock (outputLines) { outputLines.Add(line); } }, cancellationToken);

        if (!result.Succeeded)
        {
            return (true, result.WouldOverwriteExistingFiles
                ? $"'{name}' already exists in {directory}. Choose a different name."
                : $"Adding {name} failed (exit code {result.ExitCode}).{Environment.NewLine}{Tail(outputLines)}");
        }

        // Same courtesy the New Item dialog does: the file is a project item by glob, but the Roslyn
        // workspace only learns about it when told - and the model may well read it back next turn.
        foreach (var file in result.CreatedFiles.Where(f => Path.GetExtension(f) is ".cs" or ".vb"))
        {
            await _roslynWorkspaceService.NotifyDocumentAddedAsync(file, projectPath!, cancellationToken);
        }

        return (false,
            $"Added {string.Join(", ", result.CreatedFiles.Select(Path.GetFileName))} to " +
            $"{Path.GetFileName(projectPath)} in namespace {plan.DesiredNamespace}.");
    }

    /// <summary>
    /// Resolves a short name to one template, refusing the two cases worth refusing by name: a
    /// template that is not installed (which this cannot fix - installing a package is
    /// user-initiated) and a solution template (which would replace the open solution rather than
    /// add to it).
    /// </summary>
    private async Task<(TemplateInfo? Template, string? Error)> ResolveTemplateAsync(
        string shortName, TemplateKind wanted, CancellationToken cancellationToken)
    {
        var groups = await _templateCatalog.GetGroupsAsync(null, _solutionDirectory, cancellationToken);

        var group = groups.FirstOrDefault(g =>
            g.Templates.Any(t => t.ShortNames.Contains(shortName, StringComparer.OrdinalIgnoreCase)));

        if (group is null)
        {
            return (null,
                $"No template with the short name '{shortName}' is installed. Use list_templates to " +
                "see what is, or ask the user to install a template package from Tools > Template " +
                "Packages - this tool cannot install one.");
        }

        if (group.Kind == TemplateKind.Solution)
        {
            return (null,
                $"'{shortName}' is a solution template: it creates its own solution file and several " +
                "projects, so it would replace the solution that is open rather than adding to it. " +
                "Ask the user to run it from File > New > Project instead.");
        }

        if (group.Kind != wanted)
        {
            return (null,
                $"'{shortName}' is a {group.Kind.ToString().ToLowerInvariant()} template, not a " +
                $"{wanted.ToString().ToLowerInvariant()} one.");
        }

        return (group.Resolve(group.Languages.FirstOrDefault()), null);
    }

    /// <summary>The tail rather than the head: a template failure says why on its last lines, and the
    /// restore chatter above it is not what went wrong.</summary>
    private static string Tail(List<string> outputLines)
    {
        lock (outputLines)
        {
            var text = string.Join(Environment.NewLine, outputLines);
            return text.Length <= PublishTools.MaxRawOutputChars
                ? text
                : text[^PublishTools.MaxRawOutputChars..];
        }
    }
}
