using System.Text.RegularExpressions;

namespace IdePlusPlus.Tasks;

/// <summary>
/// Lists a Gruntfile's tasks by parsing <c>grunt --no-color --help</c>. Grunt has no
/// <c>--tasks-simple</c> equivalent, so the human-readable help output is the only listing it
/// offers - which is why the parsing lives in its own file, the same containment argument as
/// TrxParser: when a grunt release changes that layout, this is the one place to fix.
///
/// Loads the Gruntfile to build the list, so the same approval gate as gulp applies - see
/// <see cref="JavaScriptTaskTrustStore"/>.
/// </summary>
public static partial class GruntTaskDiscovery
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The section header grunt prints above the task list.</summary>
    [GeneratedRegex(@"^\s*Available tasks\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AvailableTasksHeaderRegex();

    /// <summary>An entry is an indented name followed by two or more spaces and its description;
    /// requiring that gap is what stops a wrapped description line being read as a task.</summary>
    [GeneratedRegex(@"^\s+(?<name>[^\s]+)\s{2,}\S", RegexOptions.Compiled)]
    private static partial Regex TaskEntryRegex();

    public static async Task<JavaScriptTaskListResult> ListTasksAsync(
        string gruntfilePath, CancellationToken cancellationToken = default)
    {
        if (Path.GetDirectoryName(gruntfilePath) is not { Length: > 0 } workingDirectory)
        {
            return JavaScriptTaskListResult.Failed($"{gruntfilePath} has no containing directory.");
        }

        var result = await NodeToolRunner.RunAsync(
            "grunt", ["--no-color", "--help"], workingDirectory, Timeout, cancellationToken);

        if (!result.Started)
        {
            return JavaScriptTaskListResult.Failed(result.StartFailure!);
        }

        if (result.TimedOut)
        {
            return JavaScriptTaskListResult.Failed(
                $"grunt did not finish listing tasks within {Timeout.TotalSeconds:0} seconds - stopped it.");
        }

        // grunt --help exits non-zero in some versions even when it printed the list, so the output
        // is parsed regardless and only an empty parse is treated as a failure.
        var taskNames = ParseTaskNames(result.Output);
        if (taskNames.Count == 0)
        {
            return JavaScriptTaskListResult.Failed(
                $"grunt listed no tasks (exit code {result.ExitCode}): {GulpTaskDiscovery.Summarize(result.Output)}");
        }

        return new JavaScriptTaskListResult(taskNames, Error: null);
    }

    /// <summary>
    /// Reads the entries under grunt's "Available tasks" heading:
    ///
    /// <code>
    /// Available tasks
    ///         concat  Concatenate files. *
    ///         uglify  Minify files with UglifyJS. *
    ///        default  Alias for "concat", "uglify" tasks.
    /// </code>
    ///
    /// The section ends at the first line that is not an indented entry, so the options block grunt
    /// prints afterwards is not read as more tasks.
    /// </summary>
    public static IReadOnlyList<string> ParseTaskNames(string output)
    {
        var taskNames = new List<string>();
        var inSection = false;

        foreach (var line in output.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (!inSection)
            {
                inSection = AvailableTasksHeaderRegex().IsMatch(line);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            var entry = TaskEntryRegex().Match(line);
            if (!entry.Success)
            {
                break;
            }

            var name = entry.Groups["name"].Value;
            if (!taskNames.Contains(name, StringComparer.Ordinal))
            {
                taskNames.Add(name);
            }
        }

        return taskNames;
    }
}
