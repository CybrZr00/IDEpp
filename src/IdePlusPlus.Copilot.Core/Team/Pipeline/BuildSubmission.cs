namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>What an owner says when it is done building its files: what it built, and anything it could
/// not do. This is the owner's whole report - the code is on the team branch, and nothing else about
/// the work is kept.</summary>
public sealed record BuildSubmission(string? Summary, IReadOnlyList<string>? Unmet = null);

public static class BuildSubmissionValidator
{
    public const int MaxSummaryLength = 600;
    public const int MaxUnmetItems = 10;
    public const int MaxUnmetLength = 300;

    /// <summary>
    /// A report needs a summary: an owner that says nothing about what it did leaves the judges and the
    /// user with only the diff. What it could not do is optional, but each item is kept short and the
    /// list is capped, so one verbose owner cannot fill a later agent's prompt.
    /// </summary>
    public static PipelineCheck<BuildSubmission> Validate(BuildSubmission? submission)
    {
        if (submission is null)
        {
            return PipelineCheck<BuildSubmission>.Failed(["The report is empty."]);
        }

        var errors = new ErrorList();
        var summary = Cut(submission.Summary, MaxSummaryLength);
        if (summary.Length == 0)
        {
            errors.Add("The report needs a summary: what you built, in a sentence or two.");
        }

        var unmet = new List<string>();
        var raw = submission.Unmet ?? [];
        if (raw.Count > MaxUnmetItems)
        {
            errors.Add($"{raw.Count} items under 'unmet' is too many; list the {MaxUnmetItems} that matter most.");
        }

        foreach (var item in raw.Take(MaxUnmetItems))
        {
            var text = Cut(item, MaxUnmetLength);
            if (text.Length > 0)
            {
                unmet.Add(text);
            }
        }

        return errors.ResultOr(() => new BuildSubmission(summary, unmet));
    }

    private static string Cut(string? text, int max) =>
        PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(text)), max, out _).TrimEnd();
}
