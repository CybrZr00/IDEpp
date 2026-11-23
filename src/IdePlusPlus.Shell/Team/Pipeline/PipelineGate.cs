using System.IO;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.TestRunner;

namespace IdePlusPlus.Shell.Team.Pipeline;

/// <summary>What the gate is asked to check: one commit, the test projects to run (absolute paths in the user's repository, which
/// the gate maps into its own checkout), and the C# files the run changed, relative to the solution folder, for the conformity
/// check.</summary>
internal sealed record GateRequest(string Commit, IReadOnlyList<string> TestProjectPaths, IReadOnlyList<string> ChangedFiles);

/// <summary>One build of one commit: whether it passed, a one-line summary, the full report as a model reads it, and the errors.</summary>
internal sealed record GateBuild(bool Ok, string Summary, string Report, IReadOnlyList<GateDiagnostic> Errors);

/// <summary>
/// The deterministic gate - "J0": builds a commit of the team branch, runs the tests, and checks the files the run changed for
/// conformity, in a checkout of its own. No model is involved and nothing it finds is an opinion: a build that fails, failed.
///
/// It works in the detached <c>_gate</c> worktree, which belongs to nothing else. That matters for two reasons. The conformity check
/// runs <c>dotnet format style</c>, which rewrites files, and those rewrites must never reach the team branch - a detached checkout
/// has no branch to commit to, and the gate puts it back to the commit it was given when it is done, so even a rewrite is gone.
/// And a build leaves output behind that an agent's worktree should not carry. The checkout is made once and kept for the run, so
/// each gate builds incrementally.
///
/// One gate run at a time (it is one worktree), each step holding one of the run's shared build slots like any agent's build.
/// </summary>
internal sealed class PipelineGate(
    RunWorkspace workspace,
    SemaphoreSlim buildSlots,
    string configuration,
    Func<IReadOnlyList<string>, string, CancellationToken, Task<DotnetRunResult>>? runDotnet = null,
    TimeSpan? buildTimeout = null,
    TimeSpan? testTimeout = null)
{
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private WorkspaceWorktree? _worktree;

    /// <summary>The gate's checkout, once it has been made.</summary>
    public WorkspaceWorktree? Worktree => _worktree;

    /// <summary>The gate's checkout, made on first use at the run's starting commit.</summary>
    public async Task<WorkspaceWorktree> EnsureWorktreeAsync(CancellationToken cancellationToken) =>
        _worktree ??= await workspace.AddDetachedWorktreeAsync(
            RunWorkspace.GateFolderName, workspace.Start, linkDependencies: true, cancellationToken);

    /// <summary>
    /// Only the build, of <paramref name="commit"/>: the check that the solution builds before any agent has been given a
    /// model call. The checkout is left at the commit.
    /// </summary>
    public async Task<GateBuild> BuildAsync(string commit, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var worktree = await EnsureWorktreeAsync(cancellationToken);
            await workspace.MoveDetachedAsync(worktree, commit, cancellationToken);
            return await BuildCoreAsync(worktree, cancellationToken);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Everything the gate checks, of one commit. The checkout is put back to the commit afterwards, whatever the checks did to it.</summary>
    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var worktree = await EnsureWorktreeAsync(cancellationToken);
            await workspace.MoveDetachedAsync(worktree, request.Commit, cancellationToken);
            try
            {
                var build = await BuildCoreAsync(worktree, cancellationToken);
                if (!build.Ok)
                {
                    const string skipped = "skipped: the build failed";
                    return new GateResult(
                        request.Commit, false, build.Summary, build.Errors, TestsRan: false, 0, 0, 0, [], skipped, GateConformity.NotRun(skipped));
                }

                var tests = await TestCoreAsync(worktree, request.TestProjectPaths, cancellationToken);
                var conformity = request.ChangedFiles.Count == 0
                    ? GateConformity.NotRun("no C# files were changed")
                    : await ConformityCoreAsync(worktree, request.ChangedFiles, cancellationToken);
                return new GateResult(
                    request.Commit, true, build.Summary, [], tests.Ran, tests.Passed, tests.Failed, tests.Skipped, tests.Failures, tests.Note, conformity);
            }
            finally
            {
                await ResetAsync(worktree, request.Commit);
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    // ---- Build ----

    private async Task<GateBuild> BuildCoreAsync(WorkspaceWorktree worktree, CancellationToken cancellationToken)
    {
        var context = workspace.ContextFor(worktree, []);
        var timeout = buildTimeout ?? TeamBuildTimeout.Current;
        var lines = new List<string>();
        int exitCode;

        await buildSlots.WaitAsync(cancellationToken);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                exitCode = await new BuildService().BuildAsync(
                    context.SolutionPath,
                    line => { lock (lines) { lines.Add(line); } },
                    configuration: configuration,
                    cancellationToken: limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var stopped = $"The build was still running after {timeout.TotalMinutes:0.#} min and was stopped.";
                return new GateBuild(false, stopped, stopped, []);
            }
        }
        finally
        {
            buildSlots.Release();
        }

        List<string> output;
        lock (lines)
        {
            output = [.. lines];
        }

        var report = ToolReports.FormatBuildReport(exitCode, configuration, [], output);
        var errors = output
            .Select(BuildDiagnosticParser.TryParse)
            .OfType<BuildDiagnostic>()
            .Where(d => d.Severity == BuildDiagnosticSeverity.Error)
            .Select(d => new GateDiagnostic(SolutionRelative(context.SolutionDirectory, d.FilePath), d.Line, d.Code, d.Message))
            .DistinctBy(d => (d.File, d.Line, d.Code, d.Message))
            .ToList();
        return new GateBuild(exitCode == 0, report.Split('\n', 2)[0], report, errors);
    }

    /// <summary>A path as the design spells it - relative to the solution folder, forward slashes - or null for one outside it.</summary>
    private static string? SolutionRelative(string solutionDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/');
        }

        var relative = Path.GetRelativePath(solutionDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
    }

    // ---- Tests ----

    private async Task<(bool Ran, int Passed, int Failed, int Skipped, IReadOnlyList<GateTestFailure> Failures, string? Note)> TestCoreAsync(
        WorkspaceWorktree worktree, IReadOnlyList<string> testProjects, CancellationToken cancellationToken)
    {
        if (testProjects.Count == 0)
        {
            return (false, 0, 0, 0, [], "this solution has no test projects");
        }

        var timeout = testTimeout ?? DefaultTestTimeout;
        var (passed, failed, skipped) = (0, 0, 0);
        var failures = new List<GateTestFailure>();
        var notes = new List<string>();
        var ran = false;

        await buildSlots.WaitAsync(cancellationToken);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var service = new TestRunService();
            foreach (var project in testProjects)
            {
                var path = Path.Combine(worktree.Path, Path.GetRelativePath(workspace.RepositoryRoot, project));
                var name = Path.GetFileNameWithoutExtension(path);
                if (!File.Exists(path))
                {
                    notes.Add($"{name}: not found in the checkout");
                    continue;
                }

                IReadOnlyList<TestResult> results;
                try
                {
                    results = await service.RunAsync(path, null, _ => { }, limit.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A test run that did not finish is not a pass: it is a failure the owners can read.
                    failures.Add(new GateTestFailure($"{name} (test run)", $"The test run was still going after {timeout.TotalMinutes:0.#} minutes and was stopped."));
                    failed++;
                    ran = true;
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    failures.Add(new GateTestFailure($"{name} (test run)", ex.Message));
                    failed++;
                    ran = true;
                    continue;
                }

                if (results.Count == 0)
                {
                    notes.Add($"{name}: no tests ran");
                    continue;
                }

                ran = true;
                passed += results.Count(r => r.Outcome == TestOutcome.Passed);
                skipped += results.Count(r => r.Outcome == TestOutcome.Skipped);
                foreach (var failure in results.Where(r => r.Outcome == TestOutcome.Failed))
                {
                    failed++;
                    failures.Add(new GateTestFailure(failure.FullyQualifiedName, failure.ErrorMessage));
                }
            }
        }
        finally
        {
            buildSlots.Release();
        }

        return (ran, passed, failed, skipped, failures, notes.Count == 0 ? null : string.Join("; ", notes));
    }

    // ---- Conformity ----

    private async Task<GateConformity> ConformityCoreAsync(
        WorkspaceWorktree worktree, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var context = workspace.ContextFor(worktree, []);
        var checker = new TeamConformityChecker(
            context.SolutionDirectory, context.SolutionPath, configuration, runDotnet ?? RunDotnetAsync, diagnostics: null);
        var report = await checker.CheckAsync(files, cancellationToken);
        return report.Refused
            ? GateConformity.NotRun(report.Text)
            : new GateConformity(true, report.Clean, report.Files, report.StyleProblems, report.AnalyzerProblems, report.BuildWarnings, null);
    }

    private async Task<DotnetRunResult> RunDotnetAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        await buildSlots.WaitAsync(cancellationToken);
        try
        {
            var output = new List<string>();
            var exitCode = await new BuildService().RunDotnetAsync(
                arguments, workingDirectory, line => { lock (output) { output.Add(line); } }, cancellationToken);
            return new DotnetRunResult(exitCode, string.Join('\n', output));
        }
        finally
        {
            buildSlots.Release();
        }
    }

    /// <summary>Back to the commit the checkout was given, discarding what the conformity check rewrote. Best effort: the next run
    /// moves the checkout again, and a checkout that is detached can never put a rewrite on the team branch.</summary>
    private async Task ResetAsync(WorkspaceWorktree worktree, string commit)
    {
        try
        {
            await workspace.MoveDetachedAsync(worktree, commit, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
