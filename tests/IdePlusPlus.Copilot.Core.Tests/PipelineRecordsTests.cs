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

    [Fact]
    public async Task TheDesignAndTheOwnership_SurviveBeingWrittenAndReadBack()
    {
        var store = new PipelineArtifactStore(_root);
        var design = new DesignPlan(
            "A greeter.",
            ["b1", "b2"],
            [
                new DesignFile("Lib/IGreeter.cs", "b1", "The contract.", ["Has Greet."], IsStub: true),
                new DesignFile("Lib/Greeter.cs", "b2", "Implements it.", ["Greets."]),
            ],
            [new MisuseCase("M1", "A null name.")],
            []);
        var ownership = OwnershipMap.From(design);

        await store.WriteJsonAsync("design.json", new DesignRecord(design, "abc1234"));
        await store.WriteJsonAsync("ownership.json", OwnershipRecord.From(ownership));

        var readDesign = (await store.ReadJsonAsync<DesignRecord>("design.json")).Value!;
        readDesign.ContractCommit.ShouldBe("abc1234");
        readDesign.Design.Files.Select(f => (f.Path, f.Owner, f.IsStub)).ShouldBe([("Lib/IGreeter.cs", "b1", true), ("Lib/Greeter.cs", "b2", false)]);
        readDesign.Design.MisuseCases.ShouldHaveSingleItem().Scenario.ShouldBe("A null name.");

        var readOwnership = (await store.ReadJsonAsync<OwnershipRecord>("ownership.json")).Value!;
        readOwnership.Owners.ShouldBe(["b1", "b2"]);
        readOwnership.Files["Lib/IGreeter.cs"].ShouldBe("b1");
        OwnershipMap.FromEntries(readOwnership.Files).OwnerOf("lib/greeter.cs").ShouldBe("b2");
    }

    [Fact]
    public async Task AnOwnersBuildRecord_SurvivesBeingWrittenAndReadBack()
    {
        var store = new PipelineArtifactStore(_root);
        var done = new BuildRecord("b1", ["Lib/IGreeter.cs", "Lib/Class1.cs"], true, "Built it.", ["The docs"], 2, 17, null);
        var silent = new BuildRecord("b2", ["Lib/Greeter.cs"], false, null, [], 0, 40, "b2: it answered without calling build_submit");

        await store.WriteJsonAsync("build/b1.json", done);
        await store.WriteJsonAsync("build/b2.json", silent);

        var readDone = (await store.ReadJsonAsync<BuildRecord>("build/b1.json")).Value!;
        (readDone.Owner, readDone.Submitted, readDone.Summary, readDone.Merges, readDone.Steps).ShouldBe(("b1", true, "Built it.", 2, 17));
        readDone.Files.ShouldBe(["Lib/IGreeter.cs", "Lib/Class1.cs"]);
        readDone.Unmet.ShouldBe(["The docs"]);
        var readSilent = (await store.ReadJsonAsync<BuildRecord>("build/b2.json")).Value!;
        readSilent.Submitted.ShouldBeFalse();
        readSilent.Summary.ShouldBeNull();
        readSilent.Gap.ShouldContain("build_submit");
    }

    [Fact]
    public async Task TheChangeRequests_SurviveBeingWrittenAndReadBack_WithTheirReasoningAndRuling()
    {
        var store = new PipelineArtifactStore(_root);
        var request = new ChangeRequest(
            "CR1", "b2", "b1", "Lib/A.cs", "Add an overload.", "The caller holds a span.", ChangeRequestState.Rejected,
            DateTimeOffset.Parse("2026-01-01T10:00:00Z"), DateTimeOffset.Parse("2026-01-01T10:05:00Z"))
        {
            Reasoning = "That signature is public API.",
            AppealUsed = true,
            AppealArgument = "Three callers already copy the span.",
            Arbitration = "The owner is right.",
        };

        await store.WriteJsonAsync("build/change-requests.json", new ChangeRequestsRecord([request]));

        var read = (await store.ReadJsonAsync<ChangeRequestsRecord>("build/change-requests.json")).Value!.Requests.ShouldHaveSingleItem();
        (read.Id, read.From, read.To, read.File, read.State).ShouldBe(("CR1", "b2", "b1", "Lib/A.cs", ChangeRequestState.Rejected));
        (read.Reasoning, read.AppealUsed, read.AppealArgument, read.Arbitration).ShouldBe(
            ("That signature is public API.", true, "Three callers already copy the span.", "The owner is right."));
        read.CreatedAt.ShouldBe(DateTimeOffset.Parse("2026-01-01T10:00:00Z"));
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
