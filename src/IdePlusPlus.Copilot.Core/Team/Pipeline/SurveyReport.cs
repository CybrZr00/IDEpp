using System.Text;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>Something the surveyed area already has that the goal can reuse: a pattern, a helper, a
/// convention - and the file that shows it.</summary>
public sealed record SurveyExisting(string Pattern, string File, string? Note = null);

/// <summary>Something the goal needs that does not exist yet, and why.</summary>
public sealed record SurveyNeed(string What, string Rationale, string? File = null);

/// <summary>Something that looks dead, duplicated or over-built, with the evidence (who calls it, how
/// many references). A recommendation only: nothing in the pipeline acts on a trim candidate unless the
/// design takes it into the plan.</summary>
public sealed record SurveyTrim(string What, string File, string Evidence);

/// <summary>
/// What one surveyor found in its focus area: what exists, what needs to, and what could go. Written by
/// the runner from the surveyor's submission and read by the designer, which has every survey in front
/// of it - so each one is size-capped, or a handful of verbose surveyors would fill the designer's
/// context before it starts.
/// </summary>
public sealed record SurveyReport(
    string Area,
    IReadOnlyList<SurveyExisting> Exists,
    IReadOnlyList<SurveyNeed> Needs,
    IReadOnlyList<SurveyTrim> Trims,
    string? Summary = null)
{
    /// <summary>True when the validator had to cut items or text to fit the caps.</summary>
    public bool Truncated { get; init; }

    /// <summary>The survey as the designer's prompt shows it (to be fenced by the caller).</summary>
    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine($"Area: {Area}");
        if (!string.IsNullOrWhiteSpace(Summary))
        {
            text.AppendLine($"Summary: {Summary}");
        }

        if (Exists.Count > 0)
        {
            text.AppendLine("Already exists:");
            foreach (var item in Exists)
            {
                text.AppendLine($"- {item.Pattern} ({item.File}){(item.Note is null ? "" : " - " + item.Note)}");
            }
        }

        if (Needs.Count > 0)
        {
            text.AppendLine("Needs to exist:");
            foreach (var item in Needs)
            {
                text.AppendLine($"- {item.What} - because {item.Rationale}{(item.File is null ? "" : " (" + item.File + ")")}");
            }
        }

        if (Trims.Count > 0)
        {
            text.AppendLine("Could be trimmed (recommendation only):");
            foreach (var item in Trims)
            {
                text.AppendLine($"- {item.What} ({item.File}) - evidence: {item.Evidence}");
            }
        }

        if (Truncated)
        {
            text.AppendLine("[this survey was cut to its size limit]");
        }

        return text.ToString().TrimEnd();
    }
}

public static class SurveyValidator
{
    public const int MaxAreaLength = 200;
    public const int MaxSummaryLength = 600;
    public const int MaxFieldLength = 400;
    public const int MaxItemsPerSection = 20;

    /// <summary>The most a rendered survey may come to; items past it are dropped from the end.</summary>
    public const int MaxTotalChars = 12_000;

    /// <summary>
    /// Checks a surveyor's submission. A survey that is missing what makes an item useful - a file for
    /// something that exists, a reason for something that is needed, evidence for a trim - is refused
    /// with that named; one that is merely too long is cut to the caps and flagged
    /// <see cref="SurveyReport.Truncated"/>, since the surveyor cannot know the designer's budget.
    /// </summary>
    public static PipelineCheck<SurveyReport> Validate(SurveyReport? report)
    {
        if (report is null)
        {
            return PipelineCheck<SurveyReport>.Failed(["The survey is empty."]);
        }

        var errors = new ErrorList();
        var truncated = false;

        string Field(string? value, int max = MaxFieldLength)
        {
            var text = PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(value)), max, out var cut);
            truncated |= cut;
            return text;
        }

        string? File(string? value, string where)
        {
            var path = PipelineArtifactStore.NormalizeRelativePath(value);
            if (path is null)
            {
                errors.Add($"{where}: '{PipelineText.Clean(value)}' is not a plain relative path inside the repository.");
            }

            return path;
        }

        var area = Field(report.Area, MaxAreaLength);
        if (area.Length == 0)
        {
            errors.Add("The survey needs an area: what part of the codebase it covers.");
        }

        var exists = new List<SurveyExisting>();
        foreach (var (item, index) in (report.Exists ?? []).Select((item, index) => (item, index)))
        {
            var where = PipelineText.Item("exists", index);
            var pattern = Field(item?.Pattern);
            if (pattern.Length == 0)
            {
                errors.Add($"{where} needs the pattern, helper or convention it found.");
            }

            var file = File(item?.File, $"{where}.file");
            if (file is not null && pattern.Length > 0)
            {
                exists.Add(new SurveyExisting(pattern, file, NullIfEmpty(Field(item!.Note))));
            }
        }

        var needs = new List<SurveyNeed>();
        foreach (var (item, index) in (report.Needs ?? []).Select((item, index) => (item, index)))
        {
            var where = PipelineText.Item("needs", index);
            var what = Field(item?.What);
            var rationale = Field(item?.Rationale);
            if (what.Length == 0)
            {
                errors.Add($"{where} needs a description of what is missing.");
            }

            if (rationale.Length == 0)
            {
                errors.Add($"{where} needs the rationale: why the goal needs it.");
            }

            string? file = null;
            if (!string.IsNullOrWhiteSpace(item?.File))
            {
                file = File(item.File, $"{where}.file");
            }

            if (what.Length > 0 && rationale.Length > 0)
            {
                needs.Add(new SurveyNeed(what, rationale, file));
            }
        }

        var trims = new List<SurveyTrim>();
        foreach (var (item, index) in (report.Trims ?? []).Select((item, index) => (item, index)))
        {
            var where = PipelineText.Item("trims", index);
            var what = Field(item?.What);
            var evidence = Field(item?.Evidence);
            if (what.Length == 0)
            {
                errors.Add($"{where} needs a description of what could be trimmed.");
            }

            if (evidence.Length == 0)
            {
                errors.Add($"{where} needs evidence (who calls it, how many references) - a trim without it is a guess.");
            }

            var file = File(item?.File, $"{where}.file");
            if (what.Length > 0 && evidence.Length > 0 && file is not null)
            {
                trims.Add(new SurveyTrim(what, file, evidence));
            }
        }

        if (!errors.Any && exists.Count + needs.Count + trims.Count == 0)
        {
            errors.Add("The survey has nothing in it: list what exists, what is needed, or what could be trimmed.");
        }

        if (errors.Any)
        {
            return PipelineCheck<SurveyReport>.Failed(errors.ToList());
        }

        if (exists.Count > MaxItemsPerSection || needs.Count > MaxItemsPerSection || trims.Count > MaxItemsPerSection)
        {
            truncated = true;
        }

        var capped = new SurveyReport(
            area,
            [.. exists.Take(MaxItemsPerSection)],
            [.. needs.Take(MaxItemsPerSection)],
            [.. trims.Take(MaxItemsPerSection)],
            NullIfEmpty(Field(report.Summary, MaxSummaryLength)))
        {
            Truncated = truncated,
        };

        return PipelineCheck<SurveyReport>.Ok(FitToTotal(capped));
    }

    /// <summary>Drops items from the end - trims first, then needs, then what exists - until the rendered
    /// survey fits <see cref="MaxTotalChars"/>.</summary>
    private static SurveyReport FitToTotal(SurveyReport report)
    {
        while (report.Render().Length > MaxTotalChars)
        {
            report = report.Trims.Count > 0
                ? report with { Trims = [.. report.Trims.Take(report.Trims.Count - 1)] }
                : report.Needs.Count > 0
                    ? report with { Needs = [.. report.Needs.Take(report.Needs.Count - 1)] }
                    : report.Exists.Count > 1
                        ? report with { Exists = [.. report.Exists.Take(report.Exists.Count - 1)] }
                        : report;
            report = report with { Truncated = true };

            if (report.Trims.Count == 0 && report.Needs.Count == 0 && report.Exists.Count <= 1)
            {
                break;
            }
        }

        return report;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
