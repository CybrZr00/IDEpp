namespace IdePlusPlus.Tasks;

/// <summary>The outcome of asking a JavaScript task runner what tasks it has.</summary>
/// <param name="TaskNames">What it reported, empty if it failed.</param>
/// <param name="Error">Why it failed, or null on success - shown to the user rather than swallowed,
/// because "no tasks" and "your gulpfile threw" look identical otherwise.</param>
public sealed record JavaScriptTaskListResult(IReadOnlyList<string> TaskNames, string? Error)
{
    public static JavaScriptTaskListResult Failed(string error) => new([], error);
}

/// <summary>
/// Lists a gulpfile's tasks with <c>gulp --tasks-simple</c>, which prints one task name per line.
///
/// This *executes the gulpfile* - that is how gulp builds its task graph, and there is no way to
/// enumerate tasks without it. Listing tasks in a repository is therefore running that repository's
/// code, which is why nothing here is called until the user has approved this solution: see
/// <see cref="JavaScriptTaskTrustStore"/>. Callers must not treat an absent approval as an
/// oversight to work around.
/// </summary>
public static class GulpTaskDiscovery
{
    /// <summary>Listing tasks should be near-instant; a gulpfile that takes longer than this at
    /// module scope is doing real work, which is exactly what should be cut short.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<JavaScriptTaskListResult> ListTasksAsync(
        string gulpfilePath, CancellationToken cancellationToken = default)
    {
        if (Path.GetDirectoryName(gulpfilePath) is not { Length: > 0 } workingDirectory)
        {
            return JavaScriptTaskListResult.Failed($"{gulpfilePath} has no containing directory.");
        }

        var result = await NodeToolRunner.RunAsync(
            "gulp", ["--tasks-simple"], workingDirectory, Timeout, cancellationToken);

        if (!result.Started)
        {
            return JavaScriptTaskListResult.Failed(result.StartFailure!);
        }

        if (result.TimedOut)
        {
            return JavaScriptTaskListResult.Failed(
                $"gulp did not finish listing tasks within {Timeout.TotalSeconds:0} seconds - stopped it. " +
                "A gulpfile that starts a watcher when it loads will do this.");
        }

        if (result.ExitCode != 0)
        {
            return JavaScriptTaskListResult.Failed($"gulp exited with code {result.ExitCode}: {Summarize(result.Output)}");
        }

        return new JavaScriptTaskListResult(ParseTaskNames(result.Output), Error: null);
    }

    /// <summary>One name per line. Anything with whitespace in it is not a task name - it is gulp
    /// or node saying something else on the same stream - so it is dropped rather than listed as a
    /// task nobody can run.</summary>
    public static IReadOnlyList<string> ParseTaskNames(string output) =>
        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && !line.Any(char.IsWhiteSpace))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal static string Summarize(string output)
    {
        var firstMeaningfulLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Length > 0);

        return firstMeaningfulLine ?? "no output";
    }
}
