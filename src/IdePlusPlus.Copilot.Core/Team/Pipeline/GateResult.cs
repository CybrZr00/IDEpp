namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>One compiler error the gate's build reported. <paramref name="File"/> is relative to the solution folder, as the
/// design spells paths, or null for one in a file outside it (or none).</summary>
public sealed record GateDiagnostic(string? File, int Line, string Code, string Message);

public sealed record GateTestFailure(string Name, string? Message);

/// <summary>
/// What the conformity check found in the files the run changed. It is advisory: <c>dotnet format</c> style fixes are made in
/// the gate's own throwaway checkout, never on the team branch, so they are reported and never block a pass.
/// </summary>
public sealed record GateConformity(
    bool Ran,
    bool Clean,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Style,
    IReadOnlyList<string> Analyzers,
    IReadOnlyList<string> Warnings,
    string? Note)
{
    public static GateConformity NotRun(string note) => new(false, true, [], [], [], [], note);
}

/// <summary>
/// What the deterministic gate made of one commit of the team branch: whether it builds, whether its tests pass, and what the
/// conformity check noticed. The gate is not an agent - it is "J0" - and its word is final for what it checks: a commit that
/// does not build or fails a test is not a pass, whatever the judges think.
/// </summary>
public sealed record GateResult(
    string Commit,
    bool BuildOk,
    string BuildSummary,
    IReadOnlyList<GateDiagnostic> BuildErrors,
    bool TestsRan,
    int TestsPassed,
    int TestsFailed,
    int TestsSkipped,
    IReadOnlyList<GateTestFailure> FailedTests,
    string? TestNote,
    GateConformity Conformity)
{
    /// <summary>It builds and no test failed. Conformity does not count; tests that could not run (none exist) do not either.</summary>
    public bool Ok => BuildOk && TestsFailed == 0;

    /// <summary>One line for the stage's record: "builds; 12 test(s) passed" or "does not build (2 error(s))".</summary>
    public string Describe()
    {
        if (!BuildOk)
        {
            return $"does not build ({BuildErrors.Count} error(s))";
        }

        var tests = !TestsRan
            ? $"tests not run{(TestNote is { Length: > 0 } ? $" ({TestNote})" : "")}"
            : TestsFailed > 0 ? $"{TestsFailed} test(s) failed, {TestsPassed} passed" : $"{TestsPassed} test(s) passed";
        var conformity = Conformity.Ran
            ? Conformity.Clean ? "conforming" : $"{Conformity.Style.Count + Conformity.Analyzers.Count + Conformity.Warnings.Count} conformity note(s)"
            : "conformity not run";
        return $"builds; {tests}; {conformity}";
    }
}

/// <summary>The gate's result with the findings it became (gate.json), so a later loop and the report read both.</summary>
public sealed record GateRecord(GateResult Result, IReadOnlyList<JudgeFinding> Findings);

/// <summary>
/// What a gate result means for the finding ledger. The gate reports as the judge "J0" and its findings are checked already -
/// the build did fail, the test did fail - so they go in open, with no quote to verify. A build error or a failed test is high
/// and blocks; whatever the conformity check noticed is low and never does.
/// </summary>
public static class GateFindings
{
    public const string Judge = "J0";

    /// <summary>The most findings of one kind: a broken build can print a hundred errors from one mistake, and the owner needs the
    /// first few.</summary>
    public const int MaxPerKind = 20;

    private const int MaxTitle = 120;
    private const int MaxEvidence = 400;

    public static IReadOnlyList<ValidatedFinding> From(GateResult result)
    {
        var findings = new List<ValidatedFinding>();

        if (!result.BuildOk)
        {
            if (result.BuildErrors.Count == 0)
            {
                findings.Add(Open(
                    "The solution does not build", TeamFindingSeverity.High, "build", [], result.BuildSummary));
            }

            foreach (var error in result.BuildErrors
                         .DistinctBy(e => (e.File, e.Code, e.Message))
                         .Take(MaxPerKind))
            {
                findings.Add(Open(
                    $"{error.Code}: {error.Message}",
                    TeamFindingSeverity.High,
                    "build",
                    error.File is null ? [] : [error.File],
                    $"{(error.File ?? "(no file)")}({error.Line}): error {error.Code}: {error.Message}"));
            }
        }

        foreach (var failure in result.FailedTests.Take(MaxPerKind))
        {
            findings.Add(Open(
                $"Test failed: {failure.Name}", TeamFindingSeverity.High, "test", [], failure.Message ?? "The test failed with no message."));
        }

        if (result.Conformity is { Ran: true, Clean: false } conformity)
        {
            AddConformity(findings, "style", conformity.Style, conformity.Files);
            AddConformity(findings, "analyzer", conformity.Analyzers, conformity.Files);
            AddConformity(findings, "build-warning", conformity.Warnings, conformity.Files);
        }

        return findings;
    }

    private static void AddConformity(List<ValidatedFinding> findings, string category, IReadOnlyList<string> lines, IReadOnlyList<string> files)
    {
        foreach (var line in lines.Take(MaxPerKind))
        {
            var text = PipelineText.Clean(line);
            var file = files.FirstOrDefault(f => text.Replace('\\', '/').Contains(f, StringComparison.OrdinalIgnoreCase));
            findings.Add(Open($"{category}: {text}", TeamFindingSeverity.Low, category, file is null ? [] : [file], text));
        }
    }

    private static ValidatedFinding Open(string title, TeamFindingSeverity severity, string category, IReadOnlyList<string> files, string evidence) =>
        new(
            new JudgeFinding(
                PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(title)), MaxTitle, out _),
                severity,
                category,
                files,
                PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(evidence)), MaxEvidence, out _)),
            FindingStatus.Open);
}
