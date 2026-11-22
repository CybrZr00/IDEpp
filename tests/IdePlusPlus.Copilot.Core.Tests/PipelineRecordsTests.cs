using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>What the runner writes to the run folder: that the roster becomes a record faithfully, and
/// that each record survives being written and read back - which is the only way anyone sees it.</summary>
public sealed class PipelineRecordsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-records-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static RosterPlan Roster()
    {
        var decision = new RosterDecision(
            [new SurveyFocus("Lib/Parsing", "How are errors reported?"), new SurveyFocus("Tests")],
            [
                new CustomJudgeSpec("Accessibility judge", "Every control has a name.", "testsOnly", ["Lib/Ui.cs"]),
                new CustomJudgeSpec("Docs judge", "Public members are documented."),
            ],
            "Two areas and two risks.");
        return RosterValidator.Validate(decision, new RosterLimits(3, 2)).Value!;
    }

    [Fact]
    public void ARosterBecomesARecord_WithEachAgentsNameAndTheScopeItWasGiven()
    {
        var record = RosterRecord.From(Roster(), decided: true, note: null);

        record.Decided.ShouldBeTrue();
        record.Rationale.ShouldBe("Two areas and two risks.");
        record.Surveyors.Select(s => (s.Name, s.Area, s.Question)).ShouldBe(
            [("u1", "Lib/Parsing", "How are errors reported?"), ("u2", "Tests", null)]);
        record.CustomJudges.Select(j => (j.Name, j.Title, j.Scope)).ShouldBe(
            [("j5", "Accessibility judge", "testsOnly"), ("j6", "Docs judge", "none")]);
        record.CustomJudges[0].FocusFiles.ShouldBe(["Lib/Ui.cs"]);
        record.CustomJudges[0].Charter.ShouldBe("Every control has a name.");
    }

    [Fact]
    public void AnUndecidedRoster_IsRecordedAsSuch_WithWhy()
    {
        var record = RosterRecord.From(new RosterPlan([], [], null), decided: false, "the lead gave no roster");

        record.Decided.ShouldBeFalse();
        record.Surveyors.ShouldBeEmpty();
        record.Note.ShouldBe("the lead gave no roster");
    }

    [Fact]
    public async Task EveryRecord_SurvivesBeingWrittenAndReadBack()
    {
        var store = new PipelineArtifactStore(_root);
        var roster = RosterRecord.From(Roster(), decided: true, note: "adjusted");
        var survey = new SurveyRecord(
            "u1", "Lib", "How is Value used?", Submitted: true, Gap: null,
            new SurveyReport("Lib", [new SurveyExisting("Value property", "Lib/Class1.cs")], [], [], "One property."));
        var missing = new SurveyRecord("u2", "Tests", null, Submitted: false, "u2: it answered without calling survey_submit", null);
        var summary = new RunSummaryRecord(
            "20260101-000000-abcd", "Make it 2.", "AllDone", 12.5, 1_000, 200,
            [
                new StageRecord("Roster", 0, 2.5, 110, []),
                new StageRecord("Understand", 0, 9, 700,
                    [new AgentRecord("u1", "Submitted", null, 0, null, 2, 2, 200, 20)], "note"),
            ],
            ["u2: no survey"]);

        await store.WriteJsonAsync("roster.json", roster);
        await store.WriteJsonAsync("survey/u1.json", survey);
        await store.WriteJsonAsync("survey/u2.json", missing);
        await store.WriteJsonAsync("summary.json", summary);

        (await store.ReadJsonAsync<RosterRecord>("roster.json")).Value.ShouldBe(roster, new RecordComparer());
        var readSurvey = (await store.ReadJsonAsync<SurveyRecord>("survey/u1.json")).Value!;
        readSurvey.Report!.Exists.ShouldHaveSingleItem().File.ShouldBe("Lib/Class1.cs");
        readSurvey.Submitted.ShouldBeTrue();
        var readMissing = (await store.ReadJsonAsync<SurveyRecord>("survey/u2.json")).Value!;
        readMissing.Report.ShouldBeNull();
        readMissing.Gap.ShouldContain("survey_submit");
        var readSummary = (await store.ReadJsonAsync<RunSummaryRecord>("summary.json")).Value!;
        readSummary.Stages.Select(s => s.Stage).ShouldBe(["Roster", "Understand"]);
        readSummary.Stages[1].Agents.ShouldHaveSingleItem().End.ShouldBe("Submitted");
        readSummary.Gaps.ShouldBe(["u2: no survey"]);
        (readSummary.Seconds, readSummary.PromptTokens).ShouldBe((12.5, 1_000L));
    }

    [Fact]
    public async Task TheRecordsOnDisk_AreCamelCaseJsonOfAnyoneToRead()
    {
        var store = new PipelineArtifactStore(_root);

        await store.WriteJsonAsync("roster.json", RosterRecord.From(Roster(), decided: true, note: null));

        var text = (await store.ReadTextAsync("roster.json"))!;
        text.ShouldContain("\"schemaVersion\": 1");
        text.ShouldContain("\"surveyors\"");
        text.ShouldContain("\"customJudges\"");
        text.ShouldContain("\"focusFiles\"");
        text.ShouldNotContain("\"note\""); // nulls are left out
    }

    /// <summary>Records hold lists, and a record's own equality compares lists by reference.</summary>
    private sealed class RecordComparer : IEqualityComparer<RosterRecord>
    {
        public bool Equals(RosterRecord? x, RosterRecord? y) =>
            x is not null && y is not null
            && x.Decided == y.Decided && x.Rationale == y.Rationale && x.Note == y.Note
            && x.Surveyors.SequenceEqual(y.Surveyors)
            && x.CustomJudges.Select(j => (j.Name, j.Title, j.Charter, j.Scope, string.Join(",", j.FocusFiles)))
                .SequenceEqual(y.CustomJudges.Select(j => (j.Name, j.Title, j.Charter, j.Scope, string.Join(",", j.FocusFiles))));

        public int GetHashCode(RosterRecord obj) => obj.Decided.GetHashCode();
    }
}
