namespace IdePlusPlus.Templates;

/// <summary>
/// Builds the <c>dotnet</c> argument lists for creating a template and adding a project to a
/// solution. Pure, and separated from the process spawning for the same reason
/// <c>PublishArguments</c> is: this is where an option that lands in the wrong position stops being
/// a typo and starts being a file written to the wrong directory.
/// </summary>
public static class TemplateCreationArguments
{
    /// <summary>
    /// Sub-commands of <c>dotnet new</c>. A template whose short name is one of these is unreachable:
    /// the implicit form runs the sub-command instead, and the explicit <c>create</c> form cannot be
    /// used (see <see cref="ForCreate"/>). None of the 228 templates in a stock .NET 10 install
    /// collides, but a third-party template package is free to.
    /// </summary>
    private static readonly HashSet<string> ReservedShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "install", "uninstall", "update", "search", "list", "details", "help",
    };

    /// <summary>
    /// <c>dotnet new &lt;short-name&gt; --name … --output …</c>.
    ///
    /// <para>Deliberately the implicit form, not <c>dotnet new create &lt;short-name&gt;</c>. Measured
    /// on the .NET 10.0.301 SDK: with the explicit <c>create</c> verb, <c>--output</c> is swallowed as
    /// a template argument and silently ignored - in *both* positions, before and after the template
    /// name - so the file is written to the working directory instead while the CLI still prints
    /// "created successfully" and exits 0. Only the implicit form honors it. This is the single
    /// easiest way to ship a New Item command that appears to work and puts every file in the wrong
    /// folder.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The template's short name collides with a
    /// <c>dotnet new</c> sub-command, and so cannot be instantiated by name at all.</exception>
    public static IReadOnlyList<string> ForCreate(TemplateCreationRequest request)
    {
        if (ReservedShortNames.Contains(request.ShortName))
        {
            throw new ArgumentException(
                $"'{request.ShortName}' is a dotnet new sub-command, so this template can't be created by name.",
                nameof(request));
        }

        var arguments = new List<string>
        {
            "new",
            request.ShortName,
            "--name",
            request.Name,
            "--output",
            request.OutputDirectory,

            // Every create would otherwise make a network call to check the template package for
            // updates - slow on a good connection and a hang risk on a bad one, for a result nothing
            // here acts on.
            "--no-update-check",
        };

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            arguments.Add("--language");
            arguments.Add(request.Language);
        }

        // The CLI unions the framework choices across the installed bands and resolves the right one
        // itself, so this is passed rather than used to pick a template identity - verified: `-f
        // net8.0` on a 10.0-default classlib does produce <TargetFramework>net8.0</TargetFramework>.
        if (!string.IsNullOrWhiteSpace(request.Framework))
        {
            arguments.Add("--framework");
            arguments.Add(request.Framework);
        }

        // Only meaningful for the templates that declare the parameter; passing it to one that
        // doesn't is an unrecognized-option error, so the caller checks DeclaresNamespaceParameter.
        if (!string.IsNullOrWhiteSpace(request.Namespace))
        {
            arguments.Add("--namespace");
            arguments.Add(request.Namespace);
        }

        foreach (var (name, value) in request.Parameters)
        {
            arguments.Add($"--{name}");

            // A bool parameter is a flag: `--actions` rather than `--actions true`. Null is how the
            // caller says "this is a switch", not "no value available".
            if (value is not null)
            {
                arguments.Add(value);
            }
        }

        return arguments;
    }

    /// <summary>
    /// <c>dotnet solution &lt;path&gt; add &lt;project&gt;</c>. Verified against a <c>.slnx</c> on the
    /// .NET 10 SDK, which is the format <c>dotnet new sln</c> now produces by default.
    /// </summary>
    /// <param name="solutionFolder">The solution folder to place the project under - this repo's own
    /// solution keeps projects in /src/ and /tests/. Null means the solution root, which needs
    /// <c>--in-root</c>: left unsaid, the CLI derives a folder from the project's path on disk.</param>
    public static IReadOnlyList<string> ForSolutionAdd(
        string solutionPath, string projectPath, string? solutionFolder)
    {
        var arguments = new List<string> { "solution", solutionPath, "add", projectPath };

        if (string.IsNullOrWhiteSpace(solutionFolder))
        {
            arguments.Add("--in-root");
        }
        else
        {
            arguments.Add("--solution-folder");
            arguments.Add(solutionFolder);
        }

        return arguments;
    }
}
