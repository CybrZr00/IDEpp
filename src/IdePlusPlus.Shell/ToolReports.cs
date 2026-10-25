using System.IO;
using System.Text;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.TestRunner;

namespace IdePlusPlus.Shell;

/// <summary>
/// How build, test and capped tool output is reported to a model - shared by the chat pane's own
/// tools and an Ultra worker's, so a worker reads a build failure in exactly the words the primary
/// conversation does and the two cannot drift.
/// </summary>
internal static class ToolReports
{
    public static string FormatBuildReport(
        int exitCode, string configuration, IReadOnlyList<string> saved, List<string> outputLines)
    {
        var diagnostics = outputLines
            .Select(BuildDiagnosticParser.TryParse)
            .OfType<BuildDiagnostic>()
            .ToList();

        var errors = diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Error).ToList();
        var warnings = diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Warning).ToList();

        var report = new StringBuilder();
        report.Append(exitCode == 0 ? $"Build succeeded ({configuration})." : $"Build FAILED ({configuration}), exit code {exitCode}.");
        report.Append($" {errors.Count} error(s), {warnings.Count} warning(s).");

        if (saved.Count > 0)
        {
            report.Append($" Saved {saved.Count} file(s) with unsaved changes first.");
        }

        // Errors first and in full; warnings only fill whatever room is left, since a build that
        // failed is about its errors.
        var reported = errors.Concat(warnings).Take(BuildTools.MaxReportedDiagnostics).ToList();
        foreach (var diagnostic in reported)
        {
            var severity = diagnostic.Severity == BuildDiagnosticSeverity.Error ? "error" : "warning";
            report.Append(
                $"\n{diagnostic.FilePath}({diagnostic.Line},{diagnostic.Column}): {severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        if (diagnostics.Count > reported.Count)
        {
            report.Append($"\n\n[{diagnostics.Count - reported.Count} more diagnostic(s) not shown]");
        }

        // BuildDiagnosticParser only matches MSBuild's "path(line,col): severity CODE:" form, so a
        // failure from MSBuild itself (MSB####), the SDK (NETSDK####), or a missing project parses
        // to nothing at all. Falling back to the raw tail is what keeps those from being reported
        // as "0 errors" on a failed build.
        if (exitCode != 0 && errors.Count == 0)
        {
            var raw = string.Join("\n", outputLines.TakeLast(60));
            if (raw.Length > BuildTools.MaxRawOutputChars)
            {
                raw = raw[^BuildTools.MaxRawOutputChars..];
            }

            report.Append($"\n\nNo compiler diagnostics were parsed. Raw build output (tail):\n{raw}");
        }

        return report.ToString();
    }

    /// <summary>Runs each test project in turn under one overall timeout and reports totals plus
    /// the first failures. The caller has already validated the projects and checked the service
    /// is idle.</summary>
    public static async Task<(bool IsError, string Text)> RunTestProjectsAsync(
        TestRunService testRunService,
        IReadOnlyList<string> projects,
        string? filter,
        TimeSpan testTimeout,
        CancellationToken cancellationToken)
    {
        var filters = string.IsNullOrWhiteSpace(filter) ? null : new[] { filter };
        var report = new StringBuilder();
        var totals = (Passed: 0, Failed: 0, Skipped: 0);
        var failures = new List<TestResult>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(testTimeout);

        foreach (var projectPath in projects)
        {
            IReadOnlyList<TestResult> results;
            try
            {
                results = await testRunService.RunAsync(projectPath, filters, _ => { }, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (true,
                    $"The test run was still going after {testTimeout.TotalMinutes:0} minutes and was stopped. "
                    + "Narrow it with 'project' or 'filter'.");
            }
            catch (InvalidOperationException ex)
            {
                return (true, ex.Message);
            }

            var name = Path.GetFileNameWithoutExtension(projectPath);
            if (results.Count == 0)
            {
                report.Append($"\n{name}: no tests ran (the project may have failed to build, or the filter matched nothing).");
                continue;
            }

            var passed = results.Count(r => r.Outcome == TestOutcome.Passed);
            var failed = results.Count(r => r.Outcome == TestOutcome.Failed);
            var skipped = results.Count(r => r.Outcome == TestOutcome.Skipped);
            totals = (totals.Passed + passed, totals.Failed + failed, totals.Skipped + skipped);
            failures.AddRange(results.Where(r => r.Outcome == TestOutcome.Failed));

            report.Append($"\n{name}: {passed} passed, {failed} failed, {skipped} skipped.");
        }

        var headline = totals.Failed == 0
            ? $"All {totals.Passed} test(s) passed."
            : $"{totals.Failed} test(s) FAILED, {totals.Passed} passed.";
        if (totals.Skipped > 0)
        {
            headline += $" {totals.Skipped} skipped.";
        }

        foreach (var failure in failures.Take(TestTools.MaxReportedFailures))
        {
            report.Append($"\n\nFAILED {failure.FullyQualifiedName}");
            if (failure.ErrorMessage is { Length: > 0 } message)
            {
                report.Append($"\n{Cap(message, TestTools.MaxFailureMessageChars, "message")}");
            }
        }

        if (failures.Count > TestTools.MaxReportedFailures)
        {
            report.Append($"\n\n[{failures.Count - TestTools.MaxReportedFailures} more failure(s) not shown]");
        }

        return (false, headline + report);
    }

    /// <summary>Length reported from the original text, not the cut copy, so the notice says how
    /// much there actually was.</summary>
    public static string Cap(string text, int maxChars, string what) =>
        text.Length <= maxChars
            ? text
            : text[..maxChars] + $"\n\n[truncated - the {what} is {text.Length} chars, showing the first {maxChars}. "
                                 + "Pass a 'path' to narrow it down.]";
}
