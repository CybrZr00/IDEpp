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
