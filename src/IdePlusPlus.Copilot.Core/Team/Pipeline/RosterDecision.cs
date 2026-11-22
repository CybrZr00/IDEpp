namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>An area one surveyor is to cover, and optionally the question to answer about it.</summary>
public sealed record SurveyFocus(string Area, string? Question = null);

/// <summary>A judge the orchestrator wants besides j1 to j4. <paramref name="Scope"/> is "none" or
/// "testsOnly" - the only two a judge can have; anything else is refused, because a charter is words
/// and the scope is what the judge can actually do.</summary>
public sealed record CustomJudgeSpec(string Title, string Charter, string Scope = "none", IReadOnlyList<string>? FocusFiles = null);

/// <summary>What the orchestrator decided the run needs: how many surveyors and where each looks, and
/// any extra judges. An empty list of surveyors means the designer surveys for itself.</summary>
public sealed record RosterDecision(
    IReadOnlyList<SurveyFocus> Surveyors,
    IReadOnlyList<CustomJudgeSpec> CustomJudges,
    string? Rationale = null);

/// <summary>The most extra agents a run lets the orchestrator add - the Settings caps.</summary>
public sealed record RosterLimits(int MaxUnderstand = 3, int MaxCustomJudges = 2);

public sealed record SurveyorAssignment(PipelineAgent Agent, SurveyFocus Focus);

public sealed record CustomJudgeAssignment(PipelineAgent Agent, IReadOnlyList<string> FocusFiles);

/// <summary>A validated roster as agents: u1.. with their focus areas, and j5.. with their charters.</summary>
public sealed record RosterPlan(
    IReadOnlyList<SurveyorAssignment> Surveyors,
    IReadOnlyList<CustomJudgeAssignment> CustomJudges,
    string? Rationale);

public static class RosterValidator
{
    public const int MaxAreaLength = 200;
    public const int MaxQuestionLength = 300;
    public const int MaxRationaleLength = 600;
    public const int MaxFocusFilesPerJudge = 12;

    /// <summary>
    /// The decision cut down to what <paramref name="limits"/> allow: the first surveyors and the first
    /// custom judges. Used when the orchestrator has been told twice that it asked for too many and the
    /// run should carry on with what fits rather than do without a roster.
    /// </summary>
    public static RosterDecision Clamp(RosterDecision decision, RosterLimits limits) =>
        decision with
        {
            Surveyors = [.. (decision.Surveyors ?? []).Take(Math.Max(0, limits.MaxUnderstand))],
            CustomJudges = [.. (decision.CustomJudges ?? []).Take(Math.Max(0, limits.MaxCustomJudges))],
        };

    /// <summary>
    /// Checks the orchestrator's roster against the caps and turns it into agents. A custom judge's
    /// scope has to be "none" or "testsOnly", its title and charter are length-capped and stripped of
    /// fence markers, and it may not reuse a standard judge's title. Surveyor areas must be distinct -
    /// two surveyors on the same area is the cost of one with half the value.
    /// </summary>
    public static PipelineCheck<RosterPlan> Validate(RosterDecision? decision, RosterLimits limits)
    {
        if (decision is null)
        {
            return PipelineCheck<RosterPlan>.Failed(["The roster is empty."]);
        }

        var errors = new ErrorList();

        var rawSurveyors = decision.Surveyors ?? [];
        if (rawSurveyors.Count > limits.MaxUnderstand)
        {
            errors.Add($"{rawSurveyors.Count} surveyors asked for; at most {limits.MaxUnderstand} are allowed in this run. "
                       + "Merge areas, or leave some to the designer.");
        }

        var surveyors = new List<SurveyorAssignment>();
        var areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, index) in rawSurveyors.Take(Math.Max(0, limits.MaxUnderstand)).Select((raw, index) => (raw, index)))
        {
            var area = Cut(raw?.Area, MaxAreaLength);
            if (area.Length == 0)
            {
                errors.Add($"{PipelineText.Item("surveyors", index)} needs an area to survey.");
                continue;
            }

            if (!areas.Add(area))
            {
                errors.Add($"Two surveyors have the same area ('{area}'); give each its own.");
                continue;
            }

            var question = Cut(raw!.Question, MaxQuestionLength);
            surveyors.Add(new SurveyorAssignment(
                PipelineRoles.UnderstanderAgent(surveyors.Count + 1),
                new SurveyFocus(area, question.Length == 0 ? null : question)));
        }

        var rawJudges = decision.CustomJudges ?? [];
        if (rawJudges.Count > limits.MaxCustomJudges)
        {
            errors.Add($"{rawJudges.Count} custom judges asked for; at most {limits.MaxCustomJudges} are allowed in this run.");
        }

        var standardTitles = PipelineRoles.StandardJudges.Select(j => j.Role.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var judges = new List<CustomJudgeAssignment>();
        foreach (var (raw, index) in rawJudges.Take(Math.Max(0, limits.MaxCustomJudges)).Select((raw, index) => (raw, index)))
        {
            var at = PipelineText.Item("customJudges", index);
            var title = DataFence.Strip(PipelineText.Clean(raw?.Title));
            var charter = DataFence.Strip(PipelineText.Clean(raw?.Charter));
            if (title.Length == 0 || charter.Length == 0)
            {
                errors.Add($"{at} needs a title and a charter: what it judges and what a good answer looks like.");
                continue;
            }

            if (title.Length > PipelineRoles.MaxJudgeTitleLength || charter.Length > PipelineRoles.MaxCustomCharterLength)
            {
                errors.Add($"{at}: keep the title under {PipelineRoles.MaxJudgeTitleLength} characters and the charter "
                           + $"under {PipelineRoles.MaxCustomCharterLength} (this one is {title.Length} and {charter.Length}).");
                continue;
            }

            if (standardTitles.Contains(title) || !titles.Add(title))
            {
                errors.Add($"{at}: the title '{title}' is already taken by another judge; name what is different about this one.");
                continue;
            }

            if (ParseScope(raw!.Scope) is not { } scope)
            {
                errors.Add($"{at}: scope '{PipelineText.Clean(raw.Scope)}' is not allowed. A judge's scope is "
                           + "\"none\" (reads and reports) or \"testsOnly\" (may also write failing tests); "
                           + "it never edits the code it judges.");
                continue;
            }

            var focusFiles = new List<string>();
            foreach (var file in (raw.FocusFiles ?? []).Take(MaxFocusFilesPerJudge))
            {
                if (PipelineArtifactStore.NormalizeRelativePath(file) is { } normalized)
                {
                    focusFiles.Add(normalized);
                }
                else
                {
                    errors.Add($"{at}: focus file '{PipelineText.Clean(file)}' is not a plain relative path.");
                }
            }

            try
            {
                judges.Add(new CustomJudgeAssignment(
                    PipelineRoles.CustomJudge(PipelineRoles.FirstCustomJudgeNumber + judges.Count, title, charter, scope),
                    focusFiles));
            }
            catch (ArgumentException ex)
            {
                errors.Add($"{at}: {ex.Message}");
            }
        }

        var rationale = Cut(decision.Rationale, MaxRationaleLength);
        return errors.ResultOr(() => new RosterPlan(surveyors, judges, rationale.Length == 0 ? null : rationale));
    }

    /// <summary>"none"/"read"/"readonly" or "testsOnly"/"tests-only"/"tests_only"/"tests", any casing;
    /// null for anything else - above all for "any", "edit" or "all".</summary>
    internal static TeamRoleEditScope? ParseScope(string? scope) =>
        new string((scope ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant() switch
        {
            "" or "none" or "read" or "readonly" => TeamRoleEditScope.None,
            "testsonly" or "tests" => TeamRoleEditScope.TestsOnly,
            _ => null,
        };

    private static string Cut(string? text, int max) =>
        PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(text)), max, out _).TrimEnd();
}
