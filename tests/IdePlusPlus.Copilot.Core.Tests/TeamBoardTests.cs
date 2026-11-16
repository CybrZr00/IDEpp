using IdePlusPlus.Copilot.Core.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TeamBoardTests
{
    private readonly ManualTimeProvider _time = new();

    private TeamBoard CreateBoard(string? journalPath = null) => new(_time, TimeSpan.FromMinutes(10), journalPath);

    [Fact]
    public void Write_NumbersEntriesAsGistIds()
    {
        var board = CreateBoard();

        var (first, _) = board.Write("w1", "FACT", "Parser lives in Core", null);
        var (second, _) = board.Write("w2", "observed", "Tokenizer has no tests", null);

        first!.Id.ShouldBe("G1");
        second!.Id.ShouldBe("G2");
        second.Type.ShouldBe(TeamBoardEntryType.Observed);
    }

    [Fact]
    public void Write_RefusesAnUnknownType_AndNamesTheValidOnes()
    {
        var (entry, error) = CreateBoard().Write("w1", "NOTE", "hello", null);

        entry.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("PATCH_SUMMARY");
    }

    [Fact]
    public void Write_RefusesTextOverTheCap_AndSaysToUseDetail()
    {
        var (entry, error) = CreateBoard().Write("w1", "FACT", new string('x', TeamBoard.MaxTextLength + 1), null);

        entry.ShouldBeNull();
        error!.ShouldContain("detail");
    }

    [Fact]
    public void Write_AllowsAPatchSummaryUpToItsLongerCap()
    {
        var text = "files=A.cs | idea=x | evidence=build ok " + new string('y', 400);

        var (entry, error) = CreateBoard().Write("w1", "PATCH_SUMMARY", text[..TeamBoard.MaxPatchSummaryLength], null);

        error.ShouldBeNull();
        entry.ShouldNotBeNull();
    }

    [Fact]
    public void Write_RefusesAPatchSummaryWithoutFilesAndEvidence()
    {
        var (entry, error) = CreateBoard().Write("w1", "PATCH_SUMMARY", "fixed the parser", null);

        entry.ShouldBeNull();
        error!.ShouldContain("evidence=");
    }

    [Fact]
    public void Read_ListsActiveClaimsFirst_AndMarksEntriesWithDetail()
    {
        var board = CreateBoard();
        board.Write("w1", "FACT", "Build is green at start", "dotnet build: 0 errors");
        board.Write("w2", "CLAIM", "Parser: add list literals", null);

        var view = board.Read();

        view.IndexOf("Active claims:", StringComparison.Ordinal)
            .ShouldBeLessThan(view.IndexOf("Entries", StringComparison.Ordinal));
        view.ShouldContain("G2 [CLAIM] w2: Parser: add list literals (lapses in 10 min)");
        view.ShouldContain("G1 [FACT] w1: Build is green at start (+detail: board_unfold G1)");
    }

    [Fact]
    public void ActiveClaims_ExpireAfterTheClaimLifetime()
    {
        var board = CreateBoard();
        board.Write("w1", "CLAIM", "Tokenizer", null);

        _time.Advance(TimeSpan.FromMinutes(9));
        board.ActiveClaims().ShouldHaveSingleItem();

        _time.Advance(TimeSpan.FromMinutes(1));
        board.ActiveClaims().ShouldBeEmpty();
    }

    [Fact]
    public void ActiveClaims_KeepsOnlyEachAuthorsLatestClaim()
    {
        var board = CreateBoard();
        board.Write("w1", "CLAIM", "Tokenizer", null);
        board.Write("w1", "CLAIM", "Parser", null);

        board.ActiveClaims().ShouldHaveSingleItem().Text.ShouldBe("Parser");
    }

    [Fact]
    public void ActiveClaims_LapseWhenTheAuthorPostsAPatchSummary()
    {
        var board = CreateBoard();
        board.Write("w1", "CLAIM", "Tokenizer", null);
        board.Write("w2", "CLAIM", "Parser", null);

        board.Write("w1", "PATCH_SUMMARY", "files=Tokenizer.cs | idea=x | evidence=tests pass", null);

        board.ActiveClaims().ShouldHaveSingleItem().Author.ShouldBe("w2");
    }

    [Fact]
    public void ReleaseClaims_EndsTheAuthorsClaim()
    {
        var board = CreateBoard();
        board.Write("w1", "CLAIM", "Tokenizer", null);

        board.ReleaseClaims("w1");

        board.ActiveClaims().ShouldBeEmpty();
    }

    [Fact]
    public void Grep_CommaIsOr_AmpersandIsAnd_CaseInsensitive()
    {
        var board = CreateBoard();
        board.Write("w1", "FAIL", "Parser rewrite broke the build", null);
        board.Write("w2", "FACT", "Parser handles comments", null);
        board.Write("w3", "OBSERVED", "Tokenizer is slow", "profiled it");

        var andResult = board.Grep("parser&BUILD");
        andResult.ShouldContain("G1");
        andResult.ShouldNotContain("G2");

        var orResult = board.Grep("comments, profiled");
        orResult.ShouldContain("G2");
        orResult.ShouldContain("G3");
        orResult.ShouldNotContain("G1");
    }

    [Fact]
    public void Grep_WithNoMatches_SaysSo()
    {
        CreateBoard().Grep("anything").ShouldContain("No board entries match");
    }

    [Fact]
    public void Unfold_ReturnsTheDetail_AndRefusesAnUnknownId()
    {
        var board = CreateBoard();
        board.Write("w1", "FAIL", "Regex approach too slow", "Benchmarked at 40s on the 2MB fixture.");

        board.Unfold("G1").ShouldContain("Benchmarked at 40s");
        board.Unfold("g1").ShouldContain("Benchmarked at 40s");
        board.Unfold("G9").ShouldContain("There is no board entry");
    }

    [Fact]
    public void TakeUnseen_ReturnsOnlyOthersEntriesOfTheGivenTypes_Once()
    {
        var board = CreateBoard();
        var types = new HashSet<TeamBoardEntryType> { TeamBoardEntryType.Fail };
        board.Write("w1", "FAIL", "mine", null);
        board.Write("w2", "FAIL", "theirs", null);
        board.Write("w2", "FACT", "not interrupting", null);

        board.TakeUnseen("w1", types).ShouldHaveSingleItem().Text.ShouldBe("theirs");
        board.TakeUnseen("w1", types).ShouldBeEmpty();

        board.Write("w3", "FAIL", "later", null);
        board.TakeUnseen("w1", types).ShouldHaveSingleItem().Text.ShouldBe("later");
    }

    [Fact]
    public void Write_AppendsEachEntryToTheJournal()
    {
        var journal = Path.Combine(Path.GetTempPath(), $"board-{Guid.NewGuid():N}.jsonl");
        try
        {
            var board = CreateBoard(journal);
            board.Write("w1", "FACT", "one", null);
            board.Write("w2", "FACT", "two", null);

            var lines = File.ReadAllLines(journal);
            lines.Length.ShouldBe(2);
            lines[1].ShouldContain("\"id\":\"G2\"");
        }
        finally
        {
            File.Delete(journal);
        }
    }

    [Fact]
    public async Task Write_FromManyThreads_KeepsEveryEntryWithUniqueIds()
    {
        var board = CreateBoard();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                board.Write($"w{worker}", "FACT", $"fact {i}", null);
            }
        })));

        var entries = board.Snapshot();
        entries.Count.ShouldBe(400);
        entries.Select(e => e.Id).Distinct().Count().ShouldBe(400);
    }

    [Fact]
    public void CycleDigest_FirstCycle_CarriesFailuresAndMerges_LaterCyclesOnlyWhatIsNew()
    {
        var board = CreateBoard();
        board.Write("w2", "FAIL", "Regex parser too slow", null);
        board.Write("w2", "PATCH_SUMMARY", "files=A.cs | idea=x | evidence=build ok", null);
        board.Write("w2", "OBSERVED", "old observation", null);

        var first = board.CycleDigest("w1");
        first.ShouldContain("Regex parser too slow");
        first.ShouldContain("files=A.cs");

        board.Write("w3", "FACT", "brand new fact", null);
        board.Write("w1", "FACT", "my own fact", null);
        var second = board.CycleDigest("w1");

        second.ShouldContain("New since your last cycle (1)");
        second.ShouldContain("brand new fact");
        second.ShouldNotContain("Regex parser too slow");
        second.ShouldNotContain("my own fact");
        board.CycleDigest("w1").ShouldContain("Nothing new since your last cycle");
    }

    [Fact]
    public void Tasks_AreOpen_ThenClaimed_ThenDone_ByNamingTheirId()
    {
        var board = CreateBoard();
        var (task, _) = board.Write(TeamMessageBus.RunnerName, "TASK", "Port the login window", null);
        board.CycleDigest("w1").ShouldContain($"{task!.Id} [TASK] runner: Port the login window");

        board.Write("w1", "CLAIM", $"{task.Id}: login window", null);
        board.CycleDigest("w2").ShouldContain("Port the login window  [claimed]");
        TeamBoard.StatusOf(task, board.Snapshot(), board.ActiveClaims()).ShouldBe(TeamTaskStatus.Claimed);

        board.Write("w1", "PATCH_SUMMARY", $"files=LoginWindow.xaml | idea={task.Id} done | evidence=build ok", null);
        TeamBoard.StatusOf(task, board.Snapshot(), board.ActiveClaims()).ShouldBe(TeamTaskStatus.Done);
        board.CycleDigest("w3").ShouldContain("0 open or claimed, 1 done");
        TeamBoard.UnfinishedTasks(board.Snapshot()).ShouldBeEmpty();
    }

    [Fact]
    public void ATaskId_IsMatchedAsAWholeWord()
    {
        var board = CreateBoard();
        var (task, _) = board.Write("runner", "TASK", "First", null);
        for (var i = 0; i < 10; i++)
        {
            board.Write("w1", "FACT", $"filler {i}", null);
        }

        // G11 must not complete G1.
        board.Write("w1", "PATCH_SUMMARY", "files=x | idea=G11 | evidence=ok", null);

        TeamBoard.StatusOf(task!, board.Snapshot(), []).ShouldBe(TeamTaskStatus.Open);
    }

    // --- Lanes and FINDINGs ---

    private const string AFinding = "severity=high kind=security evidence=branch:3f9a | ..\\..\\ read outside the worktree";

    [Fact]
    public void ALaneAndAFinding_SurviveTheJournal_AndAreCarriedThroughImport()
    {
        var journal = Path.Combine(Path.GetTempPath(), $"board-{Guid.NewGuid():N}.jsonl");
        try
        {
            var board = CreateBoard(journal);
            var (finding, error) = board.Write("adv1", "FINDING", AFinding, null, null, "adv");
            error.ShouldBeNull();
            finding!.Type.ShouldBe(TeamBoardEntryType.Finding);
            finding.Lane.ShouldBe("adv");
            TeamBoard.Format(finding).ShouldContain("[lane:adv]");

            var read = TeamBoard.ReadJournal(journal);
            var back = read.ShouldHaveSingleItem();
            back.Type.ShouldBe(TeamBoardEntryType.Finding);
            back.Lane.ShouldBe("adv");
            back.Text.ShouldBe(AFinding);

            var later = CreateBoard();
            later.Import(read, "run x");
            later.Snapshot().ShouldHaveSingleItem().Lane.ShouldBe("adv");
        }
        finally
        {
            File.Delete(journal);
        }
    }

    [Fact]
    public void Write_RefusesAFindingWithoutSeverityAndEvidence_AndSaysTheForm()
    {
        var (entry, error) = CreateBoard().Write("adv1", "FINDING", "the parser can loop forever", null, null, "adv");

        entry.ShouldBeNull();
        error!.ShouldContain("severity=");
        error.ShouldContain("evidence=");
    }

    [Fact]
    public void Write_AllowsAFindingUpToTheLongerCap()
    {
        var text = "severity=low kind=mutation evidence=score=7/9 | survivors " + new string('y', 400);

        var (entry, error) = CreateBoard().Write("mut1", "FINDING", text[..TeamBoard.MaxPatchSummaryLength], null, null, "mut");

        error.ShouldBeNull();
        entry.ShouldNotBeNull();
    }

    [Fact]
    public void TeamFinding_ReadsItsFields_AndOnlyAHighOneIsOpenHigh()
    {
        var finding = TeamFinding.TryParse(
            "severity=HIGH kind=what-if fix_by=builders evidence=tests/ParseTests.cs:12 | null input throws")!;

        finding.Severity.ShouldBe(TeamFindingSeverity.High);
        finding.SeverityName.ShouldBe("high");
        finding.Kind.ShouldBe("what-if");
        finding.FixBy.ShouldBe("builders");
        finding.Evidence.ShouldBe("tests/ParseTests.cs:12");
        TeamFinding.IsHigh(finding.Text).ShouldBeTrue();
        TeamFinding.IsHigh("severity=low kind=gap evidence=read the plan | a missing test").ShouldBeFalse();
        TeamFinding.TryParse("no fields here").ShouldBeNull();

        var board = CreateBoard();
        board.Write("adv1", "FINDING", finding.Text, null, null, "adv");
        board.Write("mut1", "FINDING", "severity=low kind=mutation evidence=score=1/4 | survivors", null, null, "mut");
        TeamBoard.Findings(board.Snapshot()).Count.ShouldBe(2);
        TeamBoard.OpenHighFindings(board.Snapshot()).ShouldHaveSingleItem().Entry.Author.ShouldBe("adv1");

        // Answering it - a PATCH_SUMMARY that names its id - clears it, the same way a TASK is done.
        board.Write("b1", "PATCH_SUMMARY", Merged("G1"), null);
        TeamBoard.OpenHighFindings(board.Snapshot()).ShouldBeEmpty();
    }

    [Fact]
    public void TheDigest_ListsTheViewersOwnLane_AndOnlyCountsTheOthers()
    {
        var board = CreateBoard();
        board.Write("runner", "TASK", "Builder work", null, null, "b");
        board.Write("runner", "TASK", "Mutation work", null, null, "mut");
        board.Write("mut1", "FINDING", "severity=low kind=mutation evidence=score=1/4 | survivors", null, null, "mut");
        board.Write("adv1", "FACT", "Adversary note", null, null, "adv");

        var builder = board.CycleDigest("b1", "b");
        builder.ShouldContain("Builder work");
        builder.ShouldNotContain("Mutation work");
        builder.ShouldContain("mut 1 other-lane TASK(s)");
        builder.ShouldContain("mut 1, adv 1 entries");

        var mut = board.CycleDigest("mut1", "mut");
        mut.ShouldContain("Mutation work");
        mut.ShouldContain("survivors");
        mut.ShouldNotContain("Builder work");

        // A worker with no lane (the lead, an older caller) still sees everything.
        var lead = board.CycleDigest("lead");
        lead.ShouldContain("Builder work");
        lead.ShouldContain("Mutation work");
    }

    [Fact]
    public void AHighFindingInterrupts_ButALowOneWaitsForTheNextBoardRead()
    {
        var board = CreateBoard();
        var bus = new TeamMessageBus(_time, ["b1", "adv1", "mut1"]);
        board.Write("adv1", "FINDING", AFinding, null, null, "adv");
        board.Write("mut1", "FINDING", "severity=low kind=mutation evidence=score=1/4 | survivors", null, null, "mut");

        var notices = TeamNotices.Compose("b1", board, bus)!;

        notices.ShouldContain("severity=high");
        notices.ShouldNotContain("score=1/4");
    }

    // --- Tasks that wait for other tasks ---

    private static string Merged(string taskId) => $"files=x | idea={taskId} done | evidence=build ok";

    [Fact]
    public void ATaskThatWaits_IsBlocked_UntilEveryTaskItWaitsForIsMerged()
    {
        var board = CreateBoard();
        var (foundation, _) = board.Write("lead", "TASK", "Add the shared helper", null);
        var (other, _) = board.Write("lead", "TASK", "Add the other helper", null);
        var (docs, error) = board.Write("lead", "TASK", "Document both", null, [foundation!.Number, other!.Number]);
        error.ShouldBeNull();
        docs!.After.ShouldBe([1, 2]);

        TeamBoard.StatusOf(docs, board.Snapshot(), board.ActiveClaims()).ShouldBe(TeamTaskStatus.Blocked);
        TeamBoard.UnmetPrerequisites(docs, board.Snapshot()).Select(p => p.Id).ShouldBe(["G1", "G2"]);

        board.Write("w1", "PATCH_SUMMARY", Merged("G1"), null);
        TeamBoard.StatusOf(docs, board.Snapshot(), board.ActiveClaims()).ShouldBe(TeamTaskStatus.Blocked);
        TeamBoard.UnmetPrerequisites(docs, board.Snapshot()).Select(p => p.Id).ShouldBe(["G2"]);

        board.Write("w2", "PATCH_SUMMARY", Merged("G2"), null);
        TeamBoard.StatusOf(docs, board.Snapshot(), board.ActiveClaims()).ShouldBe(TeamTaskStatus.Open);
    }

    [Fact]
    public void TheDigest_MarksABlockedTask_ListsTakeableOnesFirst_AndCountsWhatIsWaiting()
    {
        var board = CreateBoard();
        board.Write("lead", "TASK", "Docs for everything", null, null);
        var (foundation, _) = board.Write("lead", "TASK", "Foundation", null);
        board.Write("lead", "TASK", "Feature", null);
        // Written first on the board but waiting on a later one: it must still come after the takeable ones.
        board.Write("lead", "TASK", "Describe it", null, [foundation!.Number]);

        var digest = board.CycleDigest("w1");

        digest.ShouldContain("4 open or claimed, 0 done (1 waiting for others)");
        digest.ShouldContain("A [blocked] task cannot be claimed until what it waits for is merged");
        digest.ShouldContain("G4 [TASK] lead: Describe it  [blocked: waits for G2]");
        digest.IndexOf("G2 [TASK]", StringComparison.Ordinal).ShouldBeLessThan(digest.IndexOf("G4 [TASK]", StringComparison.Ordinal));
    }

    [Fact]
    public void WaitingOnSomethingThatIsNotATask_IsRefused_AndSoIsWaitingOnANonTask()
    {
        var board = CreateBoard();
        var (fact, _) = board.Write("w1", "FACT", "Parser lives in Core", null);

        var (entry, error) = board.Write("lead", "TASK", "Follow-up", null, [fact!.Number, 99]);
        entry.ShouldBeNull();
        error!.ShouldContain("G1, G99 are not a TASK");

        var (claim, claimError) = board.Write("w1", "CLAIM", "G1: something", null, [1]);
        claim.ShouldBeNull();
        claimError!.ShouldContain("Only a TASK can wait");
    }

    [Fact]
    public void TheDigest_SaysTheWorkIsDone_WhenEveryTaskIsMerged_AndNotBefore()
    {
        var board = CreateBoard();
        board.Write("lead", "TASK", "First", null);
        board.Write("lead", "TASK", "Second", null);
        board.Write("w1", "PATCH_SUMMARY", Merged("G1"), null);

        board.CycleDigest("w2").ShouldNotContain("Wrap up");

        board.Write("w2", "PATCH_SUMMARY", Merged("G2"), null);

        board.CycleDigest("w3").ShouldContain($"0 open or claimed, 2 done. {TeamPrompts.WrapUpNotice}");
    }

    [Theory]
    [InlineData("G4: login window", 4)]
    [InlineData("g12/w3: docs", 12)]
    [InlineData("  G7 - the helper", 7)]
    [InlineData("G4/w3: docs", 4)]
    [InlineData("w1 verify the build after G4", null)]
    [InlineData("w5 after G2: Review page", null)]
    [InlineData("G: nothing", null)]
    public void AClaimNamesItsTaskFirst_AnIdFurtherAlongIsOnlyMentioned(string claim, int? expected) =>
        TeamBoard.LeadingTaskNumber(claim).ShouldBe(expected);

    [Fact]
    public void TaskIdsInATool_AreReadAsStringsOrNumbers_AndJunkIsLeftOut()
    {
        var ids = System.Text.Json.Nodes.JsonNode.Parse("""["G4", "g5", 6, "x", "", "G4"]""");

        TeamBoard.ParseTaskIds(ids).ShouldBe([4, 5, 6]);
        TeamBoard.ParseTaskIds(null).ShouldBeEmpty();
    }

    [Fact]
    public void WhatATaskWaitsFor_SurvivesTheJournal_AndIsRenumberedOnImport_DroppingWhatWasNotCarried()
    {
        var journal = Path.Combine(Path.GetTempPath(), $"board-{Guid.NewGuid():N}.jsonl");
        try
        {
            var earlier = CreateBoard(journal);
            earlier.Write("lead", "TASK", "Done already", null);                   // G1, finished below
            earlier.Write("lead", "TASK", "Foundation", null);                      // G2
            earlier.Write("lead", "TASK", "Docs", null, [1, 2]);                    // G3 waits for both
            earlier.Write("w1", "PATCH_SUMMARY", Merged("G1"), null);

            var read = TeamBoard.ReadJournal(journal);
            read.Single(e => e.Number == 3).After.ShouldBe([1, 2]);

            // A later run carries only the unfinished tasks, so G1 is gone and G2/G3 are renumbered.
            var later = CreateBoard();
            later.Import(TeamBoard.UnfinishedTasks(read), "run x");
            var carried = later.Snapshot();

            carried.Select(e => e.Id).ShouldBe(["G1", "G2"]);
            carried[1].After.ShouldBe([1]);
            TeamBoard.StatusOf(carried[1], carried, []).ShouldBe(TeamTaskStatus.Blocked);
        }
        finally
        {
            File.Delete(journal);
        }
    }

    [Fact]
    public void AJournal_ReadsBack_AndImportsUnderTheEarlierRunsName()
    {
        var journal = Path.Combine(Path.GetTempPath(), $"board-{Guid.NewGuid():N}.jsonl");
        try
        {
            var earlier = CreateBoard(journal);
            earlier.Write("w2", "FAIL", "global.json pin is a dead end", "tried 10.0.103");
            File.AppendAllText(journal, "{not json" + Environment.NewLine);

            var read = TeamBoard.ReadJournal(journal);
            read.ShouldHaveSingleItem().Detail.ShouldBe("tried 10.0.103");

            var later = CreateBoard();
            later.Import(read, "run 20261025-151019-4058");
            var imported = later.Snapshot().ShouldHaveSingleItem();
            imported.Author.ShouldBe("w2 (run 20261025-151019-4058)");
            imported.Type.ShouldBe(TeamBoardEntryType.Fail);
            imported.Id.ShouldBe("G1");
        }
        finally
        {
            File.Delete(journal);
        }
    }
}
