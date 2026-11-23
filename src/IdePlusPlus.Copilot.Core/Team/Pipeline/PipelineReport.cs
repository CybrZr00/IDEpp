using System.Text;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>A finding as the report shows it: what a reader needs to judge it and to act on it.</summary>
public sealed record ReportFinding(
    string Id,
    string Judge,
    TeamFindingSeverity Severity,
    string Category,
    string Title,
    IReadOnlyList<string> Files,
    string? Evidence,
    string? Quote,
    string? Fix,
    FindingStatus Status,
    int SeenCount,
    string? Note)
{
    public static ReportFinding From(LedgerFinding entry) => new(
        entry.Id,
        entry.Judge,
        entry.Finding.Severity,
        entry.Finding.Category,
        entry.Finding.Title,
        entry.Finding.Files,
        entry.Finding.Evidence,
        entry.Finding.Quote,
        entry.Finding.Fix,
        entry.Status,
        entry.SeenCount,
        entry.Note);
}

/// <summary>
/// What an Ultra Pipeline run found and did, as the user is shown it: whether it passed, and if it did not, what is still open - each
/// finding with its severity and evidence - and a list of requests the user could run next. A run that stops at its cap, or because it
/// was not making progress, is not a failure to report: the code that builds has landed, and this is what is left.
/// </summary>
/// <param name="Open">What kept it from passing: open at or above the severity bar, most severe first.</param>
/// <param name="Lower">Open but below the bar: not repaired, not blocking.</param>
/// <param name="NextPrompts">What the user could ask for next: what was deferred to them, and each open finding.</param>
public sealed record PipelineReport(
    bool Passed,
    string EndReason,
    int JudgeRounds,
    int RepairRounds,
    TeamFindingSeverity Threshold,
    IReadOnlyList<ReportFinding> Open,
    IReadOnlyList<ReportFinding> Lower,
    int Fixed,
    int Dismissed,
    int Unverified,
    IReadOnlyList<string> NextPrompts,
    IReadOnlyList<string> Gaps)
{
    public const int MaxNextPrompts = 12;

    private const int MaxEvidenceChars = 600;
    private const int MaxQuoteChars = 400;

    /// <summary>One sentence: how the run ended.</summary>
    public string Headline => Passed
        ? $"Passed: every judge was satisfied after {Rounds(JudgeRounds, "judge round")} and {Rounds(RepairRounds, "repair round")}."
        : EndReason switch
        {
            "LoopCapReached" => $"Stopped at its limit of {Rounds(RepairRounds, "repair round")} with {OpenCount} still open.",
            "NoProgress" => $"Stopped early: more repair rounds were not making progress. {OpenCount} still open.",
            var other => $"Ended early ({other}). {OpenCount} still open.",
        };

    private string OpenCount => Open.Count == 1 ? "1 finding" : $"{Open.Count} findings";

    private static string Rounds(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>The report as a file for the run folder (report.md).</summary>
    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.AppendLine("# Ultra pipeline report");
        text.AppendLine();
        text.AppendLine(Headline);
        text.AppendLine();
        text.AppendLine($"- Judge rounds: {JudgeRounds}. Repair rounds: {RepairRounds}. Severity bar: {Threshold.ToString().ToLowerInvariant()} and above.");
        text.AppendLine($"- Fixed: {Fixed}. Dismissed or left for you: {Dismissed}. Set aside as unverified: {Unverified}.");

        if (Open.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Open findings");
            foreach (var finding in Open)
            {
                text.AppendLine();
                text.AppendLine($"### [{Severity(finding)}] {Line(finding.Title)}");
                text.AppendLine();
                text.AppendLine($"{finding.Id} - found by {finding.Judge}, {finding.Status.ToString().ToLowerInvariant()}"
                                + (finding.SeenCount > 1 ? $", seen in {finding.SeenCount} rounds" : "")
                                + (finding.Files.Count > 0 ? $". Files: {string.Join(", ", finding.Files)}" : "")
                                + ".");
                if (!string.IsNullOrWhiteSpace(finding.Evidence))
                {
                    text.AppendLine();
                    text.AppendLine($"Evidence: {Cut(finding.Evidence, MaxEvidenceChars)}");
                }

                if (!string.IsNullOrWhiteSpace(finding.Quote))
                {
                    text.AppendLine();
                    text.AppendLine("```");
                    text.AppendLine(Cut(finding.Quote.Replace("```", "'''", StringComparison.Ordinal), MaxQuoteChars));
                    text.AppendLine("```");
                }

                if (!string.IsNullOrWhiteSpace(finding.Fix))
                {
                    text.AppendLine();
                    text.AppendLine($"Suggested fix: {Cut(finding.Fix, MaxEvidenceChars)}");
                }

                if (!string.IsNullOrWhiteSpace(finding.Note))
                {
                    text.AppendLine();
                    text.AppendLine($"Note: {Cut(finding.Note, MaxEvidenceChars)}");
                }
            }
        }

        if (Lower.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"## Below the bar (not repaired)");
            text.AppendLine();
            foreach (var finding in Lower)
            {
                text.AppendLine($"- [{Severity(finding)}] {Line(finding.Title)}{(finding.Files.Count > 0 ? $" ({string.Join(", ", finding.Files)})" : "")}");
            }
        }

        if (NextPrompts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Suggested next prompts");
            text.AppendLine();
            for (var i = 0; i < NextPrompts.Count; i++)
            {
                text.AppendLine($"{i + 1}. {Line(NextPrompts[i])}");
            }
        }

        if (Gaps.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Gaps in the record");
            text.AppendLine();
            foreach (var gap in Gaps)
            {
                text.AppendLine($"- {Line(gap)}");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The report in a few lines for the chat: how it ended, the most severe open findings with their evidence, and the requests the
    /// user could run next. Everything is in report.md.
    /// </summary>
    public string ToSummary(int maxFindings = 5)
    {
        var lines = new List<string> { Headline };
        if (Open.Count > 0)
        {
            lines.Add("");
            lines.Add("Open findings:");
            foreach (var finding in Open.Take(maxFindings))
            {
                lines.Add($"- [{Severity(finding)}] {Line(finding.Title)}"
                          + (finding.Files.Count > 0 ? $" ({string.Join(", ", finding.Files)})" : "")
                          + (string.IsNullOrWhiteSpace(finding.Evidence) ? "" : $" - {Cut(Line(finding.Evidence), 200)}"));
            }

            if (Open.Count > maxFindings)
            {
                lines.Add($"- ... and {Open.Count - maxFindings} more in report.md.");
            }
        }

        if (NextPrompts.Count > 0)
        {
            lines.Add("");
            lines.Add("Suggested next prompts:");
            for (var i = 0; i < Math.Min(NextPrompts.Count, maxFindings); i++)
            {
                lines.Add($"{i + 1}. {Line(NextPrompts[i])}");
            }
        }

        return string.Join("\n", lines);
    }

    private static string Severity(ReportFinding finding) => finding.Severity.ToString().ToLowerInvariant();

    /// <summary>One line: the text, with its line breaks folded.</summary>
    private static string Line(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Cut(string text, int max) =>
        text.Length <= max ? text.Trim() : text[..max].TrimEnd() + "...";
}

/// <summary>Builds the report from where the loop ended.</summary>
public static class PipelineReportBuilder
{
    public static PipelineReport Build(
        FindingLedger ledger,
        IReadOnlyList<TriageDecision> deferred,
        TeamFindingSeverity threshold,
        bool passed,
        string endReason,
        int judgeRounds,
        int repairRounds,
        IReadOnlyList<string> gaps)
    {
        var findings = ledger.Findings;
        var open = findings
            .Where(f => f.IsBlocking(threshold))
            .OrderByDescending(f => f.Finding.Severity)
            .ThenBy(f => f.FirstSeenLoop)
            .Select(ReportFinding.From)
            .ToList();
        var lower = findings
            .Where(f => f.Status is FindingStatus.Open or FindingStatus.Escalated or FindingStatus.Disputed && f.Finding.Severity < threshold)
            .OrderByDescending(f => f.Finding.Severity)
            .Select(ReportFinding.From)
            .ToList();

        var prompts = deferred
            .Select(d => d.NextPrompt)
            .Concat(findings.Where(f => f.IsBlocking(threshold)).OrderByDescending(f => f.Finding.Severity).Select(f => RepairPlanner.NextPromptFor(f.Finding)))
            .OfType<string>()
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(PipelineReport.MaxNextPrompts)
            .ToList();

        return new PipelineReport(
            passed,
            endReason,
            judgeRounds,
            repairRounds,
            threshold,
            open,
            lower,
            findings.Count(f => f.Status == FindingStatus.Fixed),
            findings.Count(f => f.Status == FindingStatus.Dismissed),
            findings.Count(f => f.Status == FindingStatus.Unverified),
            prompts,
            gaps);
    }
}
