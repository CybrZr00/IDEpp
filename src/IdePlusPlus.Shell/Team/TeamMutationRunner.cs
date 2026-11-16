using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Roslyn.Mutation;
using IdePlusPlus.SourceControl;
using IdePlusPlus.TestRunner;

namespace IdePlusPlus.Shell.Team;

/// <summary>Where a mutation run works. Deliberately not <see cref="TeamWorkerContext"/>, which the
/// Ultra runner owns and grows: the mutation runner only needs the paths, and a test can build one
/// from a temp directory without standing a worker up.</summary>
public sealed record MutationRunnerContext(
    string WorktreeRoot,
    string SolutionDirectory,
    IReadOnlyList<string> TestProjectPaths,
    string Configuration = "Debug");

/// <summary>What became of one mutant.</summary>
public enum MutantOutcome
{
    /// <summary>A test failed with the mutant in place - the tests notice it.</summary>
    Killed,

    /// <summary>Every test passed with the mutant in place - a gap the mutation tester should fill.</summary>
    Survived,

    /// <summary>The mutant did not compile, so the tests said nothing about it.</summary>
    BuildFailed,

    /// <summary>The build or test run did not finish within the mutant's timeout.</summary>
    TimedOut,
}

/// <summary>
/// Backs the Ultra mutation tester's <c>run_mutation_tests</c>: mutate a file the merge changed, one
/// mutant at a time, and report which ones the tests kill.
///
/// The file is written for each mutant inside the worker's own worktree and restored in a
/// <c>finally</c>, so a cancelled or failed mutant can never leave mutated source behind; if the
/// restore write itself fails, <c>git checkout --</c> on that one path is the fallback. Mutant builds
/// and test runs take the same shared build slots as the team's own builds, because each of them is a
/// real MSBuild over that worktree's obj/bin.
///
/// If the Stryker .NET tool is installed it is preferred, since it knows far more mutators than the
/// built-in generator; if it is not installed, or its run produces no usable report, the built-in
/// generator is used instead. Nothing here asks the user for approval: it runs the SDK and the file
/// system directly, the way the build and test tools do.
/// </summary>
public sealed class TeamMutationRunner
{
    private static readonly TimeSpan DefaultMutantTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan StrykerProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StrykerRunTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private readonly MutationRunnerContext _context;
    private readonly SemaphoreSlim _buildSlots;
    private readonly BuildService _buildService;
    private readonly TestRunService _testRunService;
    private readonly TimeSpan _mutantTimeout;
    private readonly bool _useStryker;
    private readonly Func<string, string?, CancellationToken, Task<MutantOutcome>> _executeMutant;

    public TeamMutationRunner(
        MutationRunnerContext context,
        SemaphoreSlim? buildSlots = null,
        BuildService? buildService = null,
        TestRunService? testRunService = null,
        TimeSpan? mutantTimeout = null,
        bool useStryker = true,
        Func<CancellationToken, Task<MutantOutcome>>? mutantExecutor = null)
    {
        _context = context;
        _buildSlots = buildSlots ?? new SemaphoreSlim(1, 1);
        _buildService = buildService ?? new BuildService();
        _testRunService = testRunService ?? new TestRunService();
        _mutantTimeout = mutantTimeout ?? DefaultMutantTimeout;
        _useStryker = useStryker && mutantExecutor is null;
        _executeMutant = mutantExecutor is null
            ? BuildAndTestAsync
            : (_, _, token) => mutantExecutor(token);
    }

    /// <summary>
    /// Mutation-tests one file. <paramref name="changedLines"/> is the merge's changed line ranges -
    /// only those are mutated; null mutates the whole file. <paramref name="testFilter"/> narrows
    /// `dotnet test` to the tests that matter, which is what keeps a dozen mutants from running the
    /// whole suite a dozen times.
    /// </summary>
    public async Task<(bool IsError, string Text)> RunAsync(
        string? file,
        int maxMutants = MutantGenerator.DefaultMaxMutants,
        string? testFilter = null,
        IReadOnlyList<LineRange>? changedLines = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return (true, "run_mutation_tests requires a 'file' - the source file the merge changed.");
        }

        if (WorkspaceFileReader.ResolveWithinRoot(_context.SolutionDirectory, file) is not { } fullPath
            || !File.Exists(fullPath))
        {
            return (true, $"No such file in your worktree: {file}");
        }

        if (_context.TestProjectPaths.Count == 0)
        {
            return (true, "No test projects were found in this solution, so no mutant can be killed.");
        }

        var testProject = ChooseTestProject(fullPath);
        var limit = Math.Clamp(maxMutants, 1, MutantGenerator.MaxMutants);
        var (source, encoding) = ReadSource(fullPath);

        var strykerAttempted = false;
        if (_useStryker && await IsStrykerAvailableAsync(cancellationToken))
        {
            strykerAttempted = true;
            if (await TryRunStrykerAsync(fullPath, testProject, cancellationToken) is { } strykerReport)
            {
                return (false, strykerReport);
            }
        }

        var mutants = MutantGenerator.Generate(source, changedLines, limit);
        if (mutants.Count == 0)
        {
            return (false,
                $"Mutation testing {file}: no mutable constructs inside the lines this merge changed - nothing to try. "
                + "If the change added no conditions, comparisons or calls, there is nothing to report.");
        }

        var outcomes = new List<(Mutant Mutant, MutantOutcome Outcome, string? Note)>();
        var restoreNeeded = false;
        try
        {
            foreach (var mutant in mutants)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_mutantTimeout);

                MutantOutcome outcome;
                string? note = null;
                try
                {
                    restoreNeeded = true;
                    File.WriteAllText(fullPath, mutant.ApplyTo(source), encoding);
                    outcome = await _executeMutant(testProject, testFilter, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    outcome = MutantOutcome.TimedOut;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    outcome = MutantOutcome.BuildFailed;
                    note = ex.Message;
                }
                finally
                {
                    Restore(fullPath, source, encoding);
                    restoreNeeded = false;
                }

                outcomes.Add((mutant, outcome, note));
            }
        }
        finally
        {
            if (restoreNeeded)
            {
                Restore(fullPath, source, encoding);
            }
        }

        return (false, Report(file, testProject, testFilter, outcomes, strykerAttempted));
    }

    private async Task<MutantOutcome> BuildAndTestAsync(string testProject, string? testFilter, CancellationToken cancellationToken)
    {
        await _buildSlots.WaitAsync(cancellationToken);
        try
        {
            var lines = new List<string>();
            var exitCode = await _buildService.BuildAsync(
                testProject,
                line => { lock (lines) { lines.Add(line); } },
                configuration: _context.Configuration,
                cancellationToken: cancellationToken);

            if (exitCode != 0)
            {
                return MutantOutcome.BuildFailed;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TestTimeout);

            IReadOnlyList<TestResult> results;
            try
            {
                results = await _testRunService.RunAsync(
                    testProject,
                    string.IsNullOrWhiteSpace(testFilter) ? null : [testFilter],
                    _ => { },
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return MutantOutcome.TimedOut;
            }

            return results.Any(r => r.Outcome == TestOutcome.Failed)
                ? MutantOutcome.Killed
                : MutantOutcome.Survived;
        }
        finally
        {
            _buildSlots.Release();
        }
    }

    /// <summary>The restore write, then git's own checkout of that one path if the write failed. The
    /// fallback cannot use the caller's token: it runs in a finally, often because that token was
    /// cancelled.</summary>
    private void Restore(string fullPath, string original, Encoding encoding)
    {
        try
        {
            File.WriteAllText(fullPath, original, encoding);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            var relative = Path.GetRelativePath(_context.WorktreeRoot, fullPath).Replace('\\', '/');
            new GitService().DiscardChangesAsync(_context.WorktreeRoot, relative, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Nothing more to try - the report names the file so the worker can restore it.
        }
    }

    private static (string Text, Encoding Encoding) ReadSource(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom);
        return (encoding.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0)), encoding);
    }

    /// <summary>The test project most likely to cover the file: the one that mentions the source
    /// project's name, else the first. Only a heuristic - the schema has no project argument - but it
    /// keeps a solution with many test projects from running the wrong suite for every mutant.</summary>
    private string ChooseTestProject(string fullPath)
    {
        if (_context.TestProjectPaths.Count == 1)
        {
            return _context.TestProjectPaths[0];
        }

        try
        {
            string? sourceProject = null;
            for (var directory = Path.GetDirectoryName(fullPath);
                 directory is not null && directory.Length >= _context.SolutionDirectory.Length;
                 directory = Path.GetDirectoryName(directory))
            {
                sourceProject = Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();
                if (sourceProject is not null)
                {
                    break;
                }
            }

            if (sourceProject is not null)
            {
                var stem = Path.GetFileNameWithoutExtension(sourceProject);
                var match = _context.TestProjectPaths.FirstOrDefault(
                    p => File.ReadAllText(p).Contains(stem, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return _context.TestProjectPaths[0];
    }

    // ---- Stryker ----

    private async Task<bool> IsStrykerAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StrykerProbeTimeout);
            var (exitCode, _) = await RunProcessAsync(
                "dotnet", ["stryker", "--version"], _context.SolutionDirectory, timeout.Token);
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<string?> TryRunStrykerAsync(string fullPath, string testProject, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "IdePlusPlus.Stryker", Guid.NewGuid().ToString("N"));
        try
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(testProject)!, fullPath).Replace('\\', '/');
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StrykerRunTimeout);
            var (exitCode, _) = await RunProcessAsync(
                "dotnet",
                [
                    "stryker",
                    "--project", testProject,
                    "--mutate", relative,
                    "--reporter", "json",
                    "--output", outputDirectory,
                    "--threshold-break", "0",
                    "--threshold-high", "100",
                    "--threshold-low", "0",
                ],
                Path.GetDirectoryName(testProject)!,
                timeout.Token);

            var report = Directory.EnumerateFiles(outputDirectory, "*.json", SearchOption.AllDirectories)
                .Select(TryParseStrykerReport)
                .FirstOrDefault(parsed => parsed is { Count: > 0 });
            if (report is null)
            {
                return null;
            }

            var killed = report.Count(r => r.Status == "Killed");
            var survived = report.Count(r => r.Status is "Survived" or "NoCoverage");
            var report2 = new StringBuilder();
            report2.Append($"Mutation testing {Path.GetFileName(fullPath)} with dotnet stryker (exit {exitCode}): ");
            report2.Append($"{killed} killed, {survived} survived of {report.Count} mutants.\n");
            foreach (var (line, mutator, status) in report.Take(40))
            {
                report2.Append($"\n  {status,-11} L{line}: {mutator}");
            }

            if (survived > 0)
            {
                report2.Append("\n\nSurvived mutants are gaps: write a test that fails for each one, merge it, and post a FINDING.");
            }

            return report2.ToString();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(outputDirectory))
                {
                    Directory.Delete(outputDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Stryker's JSON report, read leniently: every object that has a status and a start line
    /// counts, whatever the schema's other fields are called on this version.</summary>
    private static List<(int Line, string Mutator, string Status)>? TryParseStrykerReport(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var mutants = new List<(int, string, string)>();
            Walk(document.RootElement, mutants);
            return mutants;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Walk(JsonElement element, List<(int Line, string Mutator, string Status)> mutants)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("location", out var location)
                    && location.TryGetProperty("start", out var start)
                    && start.TryGetProperty("line", out var line) && line.TryGetInt32(out var lineNumber))
                {
                    var mutator = element.TryGetProperty("mutatorName", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString() ?? "mutant"
                        : "mutant";
                    mutants.Add((lineNumber, mutator, status.GetString() ?? "unknown"));
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, mutants);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, mutants);
                }

                break;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, output.ToString());
    }

    // ---- The report ----

    private static string Report(
        string file,
        string testProject,
        string? testFilter,
        IReadOnlyList<(Mutant Mutant, MutantOutcome Outcome, string? Note)> outcomes,
        bool strykerAttempted)
    {
        var killed = outcomes.Count(o => o.Outcome == MutantOutcome.Killed);
        var survived = outcomes.Count(o => o.Outcome == MutantOutcome.Survived);
        var unbuildable = outcomes.Count(o => o.Outcome == MutantOutcome.BuildFailed);
        var timedOut = outcomes.Count(o => o.Outcome == MutantOutcome.TimedOut);
        var scored = killed + survived;
        var score = scored == 0 ? 0 : (int)Math.Round(100.0 * killed / scored);

        var report = new StringBuilder();
        report.Append($"Mutation testing {file} against {Path.GetFileName(testProject)}");
        report.Append(string.IsNullOrWhiteSpace(testFilter) ? "" : $" (filter '{testFilter}')");
        report.Append($" - {outcomes.Count} mutant(s): {killed} killed, {survived} survived");
        if (unbuildable > 0)
        {
            report.Append($", {unbuildable} did not compile");
        }

        if (timedOut > 0)
        {
            report.Append($", {timedOut} timed out");
        }

        report.Append(scored == 0 ? "." : $". Mutation score {killed}/{scored} ({score}%).");
        if (strykerAttempted)
        {
            report.Append("\n(dotnet stryker is installed but gave no usable report this time, so the built-in generator was used.)");
        }

        foreach (var (mutant, outcome, note) in outcomes)
        {
            report.Append($"\n  {Label(outcome),-11} L{mutant.Line}: {mutant.Description}");
            if (note is { Length: > 0 })
            {
                report.Append($" ({note})");
            }
        }

        report.Append(survived > 0
            ? "\n\nSurvived mutants are gaps in the tests. Write a test that fails for each survivor (TestsOnly edits), merge it, "
              + "and post a FINDING with `severity=low kind=mutation score=k/n`."
            : "\n\nNo survivors: the tests catch every mutant in the changed lines.");
        report.Append($"\nThe original {Path.GetFileName(file)} was restored.");
        return report.ToString();
    }

    private static string Label(MutantOutcome outcome) => outcome switch
    {
        MutantOutcome.Killed => "killed",
        MutantOutcome.Survived => "survived",
        MutantOutcome.BuildFailed => "build-fail",
        _ => "timed-out",
    };
}
