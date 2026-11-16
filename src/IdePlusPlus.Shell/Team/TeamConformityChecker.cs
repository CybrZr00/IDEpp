using System.IO;
using System.Text;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Shell.Team;

/// <summary>The result of one `dotnet` invocation a conformity check made.</summary>
/// <param name="exitCode">The process exit code, or -1 when it could not be started or was stopped.</param>
/// <param name="output">Everything the process wrote, stdout and stderr interleaved.</param>
/// <param name="started">False when `dotnet` itself could not be run - a missing SDK, say - so the
/// caller can tell that apart from a command that ran and failed.</param>
public sealed record DotnetRunResult(int ExitCode, string Output, bool Started = true);

/// <summary>
/// What <see cref="TeamConformityChecker"/> found for one set of changed files. Structured, so a
/// caller can turn findings into board entries without parsing the report text.
/// </summary>
public sealed record ConformityReport(
    bool Refused,
    bool Clean,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> StyleProblems,
    IReadOnlyList<string> AnalyzerProblems,
    IReadOnlyList<string> BuildWarnings,
    IReadOnlyList<string> Diagnostics,
    string Text)
{
    /// <summary>True only when a file names nothing this check could look at - so a caller can treat
    /// the report as a tool error rather than a result.</summary>
    public bool HasProblems => StyleProblems.Count > 0 || AnalyzerProblems.Count > 0 || BuildWarnings.Count > 0;
}

/// <summary>
/// The conformity (lint) lane's one tool: <c>check_conformity(files)</c>.
///
/// It runs the SDK directly - `dotnet format style` (which fixes what it can), `dotnet format
/// analyzers --verify-no-changes`, and a build whose diagnostics are narrowed to the files in
/// question - rather than going through run_command, so a specialist never asks the user to approve
/// a command and the run keeps its "no prompts while the team works" promise.
///
/// Style is fixed in place: that is the one kind of problem the lane is allowed to correct itself.
/// Everything else - analyzer diagnostics, build warnings, and whatever get_diagnostics says - is
/// reported for the model to judge, because a fix for those can change behaviour and belongs in a
/// FINDING, not in a silent edit.
///
/// The `dotnet` runner and the get_diagnostics lookup are injectable: the dispatcher passes the
/// worker's own, a test passes a scripted one.
/// </summary>
public sealed class TeamConformityChecker
{
    /// <summary>The tool name, shared with the definition in TeamTools and the dispatcher's route.</summary>
    public const string ToolName = "check_conformity";

    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromMinutes(4);

    private const int MaxReportedPerStep = 25;

    /// <summary>get_diagnostics is a whole workspace load per file, so a long change list only gets
    /// its first few checked - the style and build passes already covered them all.</summary>
    private const int MaxDiagnosticsFiles = 5;

    private readonly string _solutionDirectory;
    private readonly string _solutionPath;
    private readonly string _configuration;
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<DotnetRunResult>> _runDotnet;
    private readonly Func<string, CancellationToken, Task<(bool IsError, string Text)>>? _diagnostics;
    private readonly TimeSpan _stepTimeout;
    private readonly BuildService _dotnet = new();

    /// <param name="solutionDirectory">The worker's worktree - every file named must resolve inside it.</param>
    /// <param name="solutionPath">The solution `.slnx`/`.sln` the worker builds, passed to dotnet format
    /// as the workspace so the command does not depend on the working directory's contents.</param>
    /// <param name="runDotnet">(arguments, working directory, token) - the SDK runner. Null uses a real
    /// `dotnet` process.</param>
    /// <param name="diagnostics">get_diagnostics, keyed by relative file path. Null leaves that step out
    /// and the report tells the worker to call the tool itself.</param>
    /// <param name="stepTimeout">How long one dotnet invocation may take. Four minutes unless a test needs it shorter.</param>
    public TeamConformityChecker(
        string solutionDirectory,
        string solutionPath,
        string configuration = "Debug",
        Func<IReadOnlyList<string>, string, CancellationToken, Task<DotnetRunResult>>? runDotnet = null,
        Func<string, CancellationToken, Task<(bool IsError, string Text)>>? diagnostics = null,
        TimeSpan? stepTimeout = null)
    {
        _solutionDirectory = solutionDirectory;
        _solutionPath = solutionPath;
        _configuration = configuration;
        _diagnostics = diagnostics;
        _stepTimeout = stepTimeout ?? DefaultStepTimeout;
        _runDotnet = runDotnet ?? RunDotnetProcessAsync;
    }

    public async Task<ConformityReport> CheckAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var (resolved, refused) = ResolveFiles(files);
        if (refused is not null)
        {
            return Refused(refused);
        }

        var relative = resolved.Select(path => Path.GetRelativePath(_solutionDirectory, path)).ToList();

        var style = await RunStepAsync(
            ["format", _solutionPath, "style", "--include", .. resolved], cancellationToken);
        if (!style.Started)
        {
            return Unavailable(relative, style);
        }

        var analyzers = await RunStepAsync(
            ["format", _solutionPath, "analyzers", "--verify-no-changes", "--include", .. resolved], cancellationToken);
        if (!analyzers.Started)
        {
            return Unavailable(relative, analyzers);
        }

        var build = await RunStepAsync(
            ["build", _solutionPath, "--configuration", _configuration], cancellationToken);
        if (!build.Started)
        {
            return Unavailable(relative, build);
        }

        var styleProblems = style.ExitCode == 0
            ? Array.Empty<string>()
            : ProblemLines(style.Output, style.ExitCode);
        var analyzerProblems = analyzers.ExitCode == 0
            ? Array.Empty<string>()
            : ProblemLines(analyzers.Output, analyzers.ExitCode);
        var buildProblems = BuildProblemsFor(build, resolved, relative);

        var diagnostics = new List<string>();
        if (_diagnostics is not null)
        {
            foreach (var file in relative.Take(MaxDiagnosticsFiles))
            {
                var (isError, text) = await _diagnostics(file, cancellationToken);
                if (text.Trim().Length == 0)
                {
                    continue;
                }

                diagnostics.Add(isError ? $"{file}: get_diagnostics failed - {text.Trim()}" : text.Trim());
            }
        }

        var clean = styleProblems.Count == 0 && analyzerProblems.Count == 0
                    && buildProblems.Count == 0 && build.ExitCode == 0;

        var report = new StringBuilder();
        report.Append($"Conformity check of {relative.Count} file(s): {string.Join(", ", relative)}");
        report.Append(styleProblems.Count == 0
            ? "\n- style: clean (dotnet format style reported nothing it could not fix)."
            : $"\n- style: {styleProblems.Count} problem(s) - see below; the style pass ran but the check did not pass.");
        report.Append(analyzerProblems.Count == 0
            ? "\n- analyzers: clean (dotnet format analyzers --verify-no-changes)."
            : $"\n- analyzers: {analyzerProblems.Count} problem(s).");
        report.Append(build.ExitCode == 0
            ? $"\n- build ({_configuration}): succeeded, {buildProblems.Count} diagnostic(s) in these files."
            : $"\n- build ({_configuration}): FAILED, {buildProblems.Count} diagnostic(s) in these files.");

        foreach (var problem in styleProblems.Concat(analyzerProblems).Concat(buildProblems).Take(MaxReportedPerStep))
        {
            report.Append($"\n  {problem}");
        }

        if (_diagnostics is null)
        {
            report.Append("\n- get_diagnostics: not run - call get_diagnostics on these files yourself.");
        }
        else if (diagnostics.Count > 0)
        {
            report.Append("\n- get_diagnostics:");
            foreach (var line in diagnostics)
            {
                report.Append($"\n  {line}");
            }
        }
        else
        {
            report.Append("\n- get_diagnostics: nothing reported for these files.");
        }

        report.Append(clean
            ? "\nVerdict: conforming. Nothing to fix here."
            : "\nVerdict: not conforming. Fix style-only problems yourself (they are mechanical and the style pass "
              + "has already touched the files), merge in a small batch, and report anything that would change "
              + "behaviour as a FINDING with severity= and evidence= instead of editing it.");

        return new ConformityReport(
            Refused: false,
            Clean: clean,
            Files: relative,
            StyleProblems: styleProblems,
            AnalyzerProblems: analyzerProblems,
            BuildWarnings: buildProblems,
            Diagnostics: diagnostics,
            Text: report.ToString());
    }

    /// <summary>
    /// Every named file must resolve inside the worker's worktree. A path that escapes it - or one
    /// that does not exist, since there is nothing to format - refuses the whole call rather than
    /// checking a subset, so a caller cannot smuggle a path out by adding a valid one beside it.
    /// </summary>
    private (IReadOnlyList<string> Resolved, string? Refused) ResolveFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return ([], "check_conformity needs at least one file - the files your review item changed.");
        }

        var resolved = new List<string>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return ([], "check_conformity was given an empty file name.");
            }

            if (WorkspaceFileReader.ResolveWithinRoot(_solutionDirectory, file.Trim()) is not { } fullPath)
            {
                return ([], $"Refused: '{file}' resolves outside your worktree's solution directory.");
            }

            if (!File.Exists(fullPath))
            {
                return ([], $"Refused: '{file}' does not exist in your worktree. Pass paths of files that do.");
            }

            if (seen.Add(fullPath))
            {
                resolved.Add(fullPath);
            }
        }

        return (resolved, null);
    }

    private async Task<DotnetRunResult> RunStepAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_stepTimeout);
        try
        {
            return await _runDotnet(arguments, _solutionDirectory, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DotnetRunResult(
                -1, $"`dotnet {arguments[0]} {arguments[1]}` was still running after "
                    + $"{_stepTimeout.TotalMinutes:0.#} min and was stopped.", Started: true);
        }
        catch (InvalidOperationException ex)
        {
            // BuildService refuses a second dotnet invocation on the same instance, and a formatter
            // failure it could not classify lands here too. Not fatal - the report says so.
            return new DotnetRunResult(-1, ex.Message, Started: true);
        }
    }

    /// <summary>Builds produce one line per project and diagnostic; only the diagnostics for the files
    /// under review matter to this lane, so everything else is dropped rather than shown.</summary>
    private IReadOnlyList<string> BuildProblemsFor(DotnetRunResult build, IReadOnlyList<string> resolved, IReadOnlyList<string> relative)
    {
        var problems = new List<string>();
        foreach (var line in build.Output.Split('\n'))
        {
            if (BuildDiagnosticParser.TryParse(line.Trim()) is not { } diagnostic)
            {
                continue;
            }

            if (!MatchFile(diagnostic.FilePath, resolved, relative, out var shown))
            {
                continue;
            }

            problems.Add($"{shown}({diagnostic.Line},{diagnostic.Column}): "
                         + $"{(diagnostic.Severity == BuildDiagnosticSeverity.Error ? "error" : "warning")} "
                         + $"{diagnostic.Code}: {diagnostic.Message}");
            if (problems.Count >= MaxReportedPerStep)
            {
                break;
            }
        }

        if (build.ExitCode != 0 && problems.Count == 0)
        {
            // A build can fail without naming one of the changed files (a project-level error, a
            // missing SDK target). Say so, or the report would look clean on a failed build.
            problems.Add($"build failed (exit code {build.ExitCode}) without a diagnostic naming these files.");
        }

        return problems;
    }

    private static bool MatchFile(
        string? diagnosticPath, IReadOnlyList<string> resolved, IReadOnlyList<string> relative, out string shown)
    {
        shown = "";
        if (string.IsNullOrWhiteSpace(diagnosticPath))
        {
            return false;
        }

        // MSBuild writes the path however the project did - absolute, or relative to the project
        // directory - so compare both the full path and the tail.
        var normalized = diagnosticPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        for (var i = 0; i < resolved.Count; i++)
        {
            if (SameFile(normalized, resolved[i]))
            {
                shown = relative[i];
                return true;
            }
        }

        return false;
    }

    private static bool SameFile(string left, string right)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable path from the build falls through to the tail comparison below.
        }

        return right.EndsWith(Path.DirectorySeparatorChar + left, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ProblemLines(string output, int exitCode)
    {
        var lines = output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var problems = lines
            .Where(line => line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (problems.Count == 0)
        {
            problems = lines;
        }

        if (problems.Count == 0)
        {
            problems = [$"dotnet exited with code {exitCode} and wrote nothing."];
        }

        return [.. problems.Take(MaxReportedPerStep)];
    }

    private static ConformityReport Refused(string reason) => new(
        Refused: true,
        Clean: false,
        Files: [],
        StyleProblems: [],
        AnalyzerProblems: [],
        BuildWarnings: [],
        Diagnostics: [],
        Text: reason);

    private static ConformityReport Unavailable(IReadOnlyList<string> relative, DotnetRunResult result) => new(
        Refused: false,
        Clean: false,
        Files: relative,
        StyleProblems: [],
        AnalyzerProblems: [],
        BuildWarnings: [],
        Diagnostics: [],
        Text: "The conformity check could not run `dotnet`: " + (result.Output.Trim().Length > 0
            ? result.Output.Trim()
            : "it did not start.")
            + "\nIs the SDK installed and on PATH? Until then, rely on build_solution and get_diagnostics.");

    /// <summary>The real runner: BuildService already owns how a `dotnet` child is started, how its
    /// output is streamed and how its process tree is killed on cancel, so this only collects it.</summary>
    private async Task<DotnetRunResult> RunDotnetProcessAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        var exitCode = await _dotnet.RunDotnetAsync(
            arguments,
            workingDirectory,
            line => { lock (output) { output.Add(line); } },
            cancellationToken);
        return new DotnetRunResult(exitCode, string.Join('\n', output));
    }
}
