using System.Text;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>What one owner reported in a repair round: what it fixed and what it could not.</summary>
public sealed record RepairNote(string Owner, IReadOnlyList<string> Findings, string? Summary, IReadOnlyList<string> Unmet);

/// <summary>
/// One round of the loop, as the next round is told of it: what the gate and the judges said in numbers, and what was done about it.
/// It carries no judge's findings: a judge is never shown another's, and what each judge itself found it is shown on its own.
/// </summary>
public sealed record LoopHistoryEntry(
    int Round,
    string Gate,
    bool JudgesRan,
    int JudgesPass,
    int JudgesFail,
    int JudgesUnavailable,
    int Open,
    int Score,
    IReadOnlyList<RepairNote> Repairs,
    IReadOnlyList<string> Dismissed,
    IReadOnlyList<string> Deferred)
{
    public static LoopHistoryEntry Begin(
        int round, string gate, bool judgesRan, int pass, int fail, int unavailable, int open, int score) =>
        new(round, gate, judgesRan, pass, fail, unavailable, open, score, [], [], []);
}

/// <summary>
/// The history of the loop in a few lines (digest.md), for the prompts of the next round: how each round went, what each owner
/// said it did and could not do - so an owner told to repair something again can see what was tried - and what the lead dismissed or
/// left to the user, so no judge is asked to look at it again. At most <see cref="MaxChars"/>: when there is more, the earliest rounds
/// are left out, since the latest are the ones that matter.
/// </summary>
public static class PipelineDigest
{
    public const int MaxChars = 3_000;

    private const int MaxItemChars = 160;

    public static string Render(IReadOnlyList<LoopHistoryEntry> history, int maxChars = MaxChars)
    {
        if (history.Count == 0)
        {
            return "";
        }

        var rounds = history.Select(RenderRound).ToList();
        var first = 0;
        const string Omitted = "(Earlier rounds are left out of this history.)\n";
        while (first < rounds.Count - 1 && Length(rounds, first) + (first > 0 ? Omitted.Length : 0) > maxChars)
        {
            first++;
        }

        var text = (first > 0 ? Omitted : "") + string.Join("\n", rounds.Skip(first));
        return text.Length <= maxChars ? text.TrimEnd() : text[..Math.Max(0, maxChars - 3)].TrimEnd() + "...";
    }

    private static int Length(List<string> rounds, int from) => rounds.Skip(from).Sum(r => r.Length + 1);

    private static string RenderRound(LoopHistoryEntry entry)
    {
        var text = new StringBuilder();
        var judges = entry.JudgesRan
            ? $"judges: {entry.JudgesPass} pass, {entry.JudgesFail} fail"
              + (entry.JudgesUnavailable > 0 ? $", {entry.JudgesUnavailable} gave no verdict" : "")
            : "judges: not run";
        text.AppendLine($"Round {entry.Round}: gate: {Cut(entry.Gate)}; {judges}; still open: {entry.Open} (score {entry.Score}).");
        foreach (var repair in entry.Repairs)
        {
            var fixedIds = repair.Findings.Count > 0 ? $" for {string.Join(", ", repair.Findings)}" : "";
            text.AppendLine($"  {repair.Owner} repaired{fixedIds}: {(string.IsNullOrWhiteSpace(repair.Summary) ? "(no report)" : Cut(repair.Summary))}");
            foreach (var unmet in repair.Unmet)
            {
                text.AppendLine($"    could not: {Cut(unmet)}");
            }
        }

        foreach (var dismissed in entry.Dismissed)
        {
            text.AppendLine($"  dismissed by the lead: {Cut(dismissed)}");
        }

        foreach (var deferred in entry.Deferred)
        {
            text.AppendLine($"  left to the user: {Cut(deferred)}");
        }

        return text.ToString();
    }

    private static string Cut(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= MaxItemChars ? line : line[..MaxItemChars].TrimEnd() + "...";
    }
}
