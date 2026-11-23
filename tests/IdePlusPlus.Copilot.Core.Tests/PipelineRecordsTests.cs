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
        readMissing.Gap.ShouldNotBeNull().ShouldContain("survey_submit");
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
        readSilent.Gap.ShouldNotBeNull().ShouldContain("build_submit");
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

    [Fact]
    public async Task AVerdict_SurvivesBeingWrittenAndReadBack_WithItsFindingsCoverageAndEvidenceBranch()
    {
        var store = new PipelineArtifactStore(_root);
        var finding = new JudgeFinding(
            "A race", TeamFindingSeverity.High, "correctness", ["Lib/A.cs"], "A failing test.", "lock (_gate) { }", "Take the lock.", FindingFixBy.Design);
        var record = new VerdictRecord(
            "j4", "Runtime bugs judge", true, null, JudgeVerdictKind.Fail, "A race.",
            [new ValidatedFinding(finding, FindingStatus.Open), new ValidatedFinding(finding with { Title = "Another" }, FindingStatus.Unverified, "The quoted code was not found.")],
            [new CoverageEntry("M1", "covered", "Handled in A.")], "ideplusplus/ultra/r1/j4", "abc1234");

        await store.WriteJsonAsync("loop-1/verdicts/j4.json", record);

        var read = (await store.ReadJsonAsync<VerdictRecord>("loop-1/verdicts/j4.json")).Value!;
        (read.Judge, read.Title, read.Available, read.Unavailable, read.Verdict, read.Summary).ShouldBe(
            ("j4", "Runtime bugs judge", true, null, JudgeVerdictKind.Fail, "A race."));
        (read.EvidenceBranch, read.EvidenceCommit).ShouldBe(("ideplusplus/ultra/r1/j4", "abc1234"));
        read.Findings.Select(f => (f.Finding.Title, f.Status, f.Note)).ShouldBe(
            [("A race", FindingStatus.Open, null), ("Another", FindingStatus.Unverified, "The quoted code was not found.")]);
        var first = read.Findings[0].Finding;
        (first.Severity, first.Category, first.Evidence, first.Quote, first.Fix, first.FixBy).ShouldBe(
            (TeamFindingSeverity.High, "correctness", "A failing test.", "lock (_gate) { }", "Take the lock.", FindingFixBy.Design));
        first.Files.ShouldBe(["Lib/A.cs"]);
        read.Coverage.ShouldHaveSingleItem().ShouldBe(new CoverageEntry("M1", "covered", "Handled in A."));
    }

    [Fact]
    public async Task AJudgeThatGaveNoVerdict_IsRecordedAsUnavailable_WithWhy()
    {
        var store = new PipelineArtifactStore(_root);

        await store.WriteJsonAsync("loop-1/verdicts/j3.json", new VerdictRecord("j3", "Security judge", false, "It never called verdict_submit.", null, null, [], [], null, null));

        var read = (await store.ReadJsonAsync<VerdictRecord>("loop-1/verdicts/j3.json")).Value!;
        (read.Available, read.Verdict, read.Unavailable).ShouldBe((false, null, "It never called verdict_submit."));
        read.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFindingsOfARound_SurviveBeingWrittenAndReadBack_WithTheirHistory()
    {
        var store = new PipelineArtifactStore(_root);
        var finding = new JudgeFinding(
            "A race", TeamFindingSeverity.High, "correctness", ["Lib/A.cs"], "A failing test.", "lock (_gate) { }", "Take the lock.", FindingFixBy.Design);
        var record = new FindingsRecord(
            2, false, 4, 1,
            [
                new LedgerFinding("F1.1", "abc123abc123", "j4", finding, 1, 2, 2, FindingStatus.Escalated, "reported again after a repair (seen in 2 loops)"),
                new LedgerFinding("F1.2", "def456def456", "J0", finding with { Title = "CS1002: ; expected" }, 1, 1, 1, FindingStatus.Fixed, "not reported again in loop 2"),
            ]);

        await store.WriteJsonAsync("loop-2/findings.json", record);

        var read = (await store.ReadJsonAsync<FindingsRecord>("loop-2/findings.json")).Value!;
        (read.Round, read.Passed, read.Score, read.Stalled).ShouldBe((2, false, 4, 1));
        read.Findings.Select(f => (f.Id, f.Judge, f.Status, f.SeenCount, f.FirstSeenLoop, f.LastSeenLoop, f.Note)).ShouldBe(
            [
                ("F1.1", "j4", FindingStatus.Escalated, 2, 1, 2, "reported again after a repair (seen in 2 loops)"),
                ("F1.2", "J0", FindingStatus.Fixed, 1, 1, 1, "not reported again in loop 2"),
            ]);
        var first = read.Findings[0].Finding;
        (first.Title, first.Severity, first.FixBy, first.Quote).ShouldBe(("A race", TeamFindingSeverity.High, FindingFixBy.Design, "lock (_gate) { }"));
        first.Files.ShouldBe(["Lib/A.cs"]);
    }

    [Fact]
    public async Task TheTriageOfARound_SurvivesBeingWrittenAndReadBack_WithEachKindOfDecision()
    {
        var store = new PipelineArtifactStore(_root);
        var record = new TriageRecord(
            2,
            [
                new TriageDecision("F2.1", TriageAction.Assign, "One owner owns it.", "b1", "Guard the null."),
                new TriageDecision("F2.2", TriageAction.Dismiss, "Not a real problem."),
                new TriageDecision("F2.3", TriageAction.Defer, "Out of scope.", NextPrompt: "Cache the parse."),
            ],
            AskedTheLead: true, LeadDecided: false, Notes: "No triage was decided (it never submitted).");

        await store.WriteJsonAsync("loop-2/triage.json", record);

        var read = (await store.ReadJsonAsync<TriageRecord>("loop-2/triage.json")).Value!;
        (read.Round, read.AskedTheLead, read.LeadDecided, read.Notes).ShouldBe((2, true, false, "No triage was decided (it never submitted)."));
        read.Decisions.Select(d => (d.FindingId, d.Action, d.Owner, d.Instruction, d.Reason, d.NextPrompt)).ShouldBe(
            [
                ("F2.1", TriageAction.Assign, "b1", "Guard the null.", "One owner owns it.", null),
                ("F2.2", TriageAction.Dismiss, null, null, "Not a real problem.", null),
                ("F2.3", TriageAction.Defer, null, null, "Out of scope.", "Cache the parse."),
            ]);
    }

    [Fact]
    public async Task ARepairRound_SurvivesBeingWrittenAndReadBack_WithWhatEachOwnerDid()
    {
        var store = new PipelineArtifactStore(_root);
        var record = new RepairRecord(
            1, true,
            [
                new RepairOwnerRecord("b1", ["F1.1", "F1.3"], true, "Fixed both.", [], 2, 14, null),
                new RepairOwnerRecord("b2", ["F1.2"], false, null, ["F1.2: needs a redesign"], 0, 3, "b2: it never called build_submit."),
            ]);

        await store.WriteJsonAsync("loop-1/repairs.json", record);

        var read = (await store.ReadJsonAsync<RepairRecord>("loop-1/repairs.json")).Value!;
        (read.Round, read.Changed).ShouldBe((1, true));
        read.Owners.Select(o => (o.Owner, o.Submitted, o.Summary, o.Merges, o.Steps, o.Gap)).ShouldBe(
            [("b1", true, "Fixed both.", 2, 14, null), ("b2", false, null, 0, 3, "b2: it never called build_submit.")]);
        read.Owners[0].Findings.ShouldBe(["F1.1", "F1.3"]);
        read.Owners[1].Unmet.ShouldBe(["F1.2: needs a redesign"]);
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
