using IdePlusPlus.Copilot.Core.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TeamMessageBusTests
{
    private readonly ManualTimeProvider _time = new();

    private TeamMessageBus CreateBus() => new(_time, ["w1", "w2", "w3"]);

    [Fact]
    public void AMemberWhoJoinsMidRun_GetsTheChannelFromThenOn_AndCanBeMessaged()
    {
        var bus = CreateBus();
        bus.Post("w1", "before w4 joined");

        bus.AddMember("w4");
        bus.Post("w1", "after w4 joined");

        bus.TakeChannel("w4").Select(m => m.Text).ShouldBe(["after w4 joined"]);
        bus.SendDirect("w2", "w4", "welcome").ShouldBeNull();
        bus.TakeDirect("w4").ShouldHaveSingleItem().Text.ShouldBe("welcome");
        bus.Members.ShouldContain("w4");
    }

    [Fact]
    public void SendDirect_IsTakenOnceByTheRecipientOnly()
    {
        var bus = CreateBus();

        bus.SendDirect("w1", "w2", "I'm editing Parser.cs").ShouldBeNull();

        bus.TakeDirect("w3").ShouldBeEmpty();
        bus.TakeDirect("w2").ShouldHaveSingleItem().From.ShouldBe("w1");
        bus.TakeDirect("w2").ShouldBeEmpty();
    }

    [Fact]
    public void SendDirect_ToAnUnknownWorker_NamesTheTeam()
    {
        var error = CreateBus().SendDirect("w1", "w9", "hello");

        error.ShouldNotBeNull();
        error.ShouldContain("w1, w2, w3");
    }

    [Fact]
    public void SendDirect_ToYourself_IsRefused()
    {
        CreateBus().SendDirect("w1", "w1", "hello").ShouldNotBeNull();
    }

    [Fact]
    public void TakeChannel_ReturnsOthersPostsSinceTheWorkersLastTake()
    {
        var bus = CreateBus();
        bus.Post("w1", "Introducing IParser in Core");
        bus.Post("w2", "Taking the tokenizer");

        bus.TakeChannel("w1").ShouldHaveSingleItem().Text.ShouldBe("Taking the tokenizer");
        bus.TakeChannel("w1").ShouldBeEmpty();
        bus.TakeChannel("w3").Count.ShouldBe(2);
    }

    [Fact]
    public void Broadcast_SendsADirectMessageToEveryMember()
    {
        var bus = CreateBus();

        bus.Broadcast(TeamMessageBus.RunnerName, "10 min left");

        bus.TakeDirect("w1").ShouldHaveSingleItem().From.ShouldBe(TeamMessageBus.RunnerName);
        bus.TakeDirect("w2").ShouldHaveSingleItem();
        bus.TakeDirect("w3").ShouldHaveSingleItem();
    }

    [Fact]
    public void Post_RefusesEmptyAndOverlongMessages()
    {
        var bus = CreateBus();

        bus.Post("w1", "  ").ShouldNotBeNull();
        bus.Post("w1", new string('x', TeamMessageBus.MaxMessageLength + 1)).ShouldNotBeNull();
        bus.ChannelSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Notices_CarryDirectMessagesAndInterruptingBoardEntries_ThenNothing()
    {
        var bus = CreateBus();
        var board = new TeamBoard(_time, TimeSpan.FromMinutes(10));
        bus.SendDirect("w2", "w1", "Leave Lexer.cs to me");
        board.Write("w2", "FAIL", "Regex lexer too slow", null);
        board.Write("w2", "FACT", "Lexer has 12 tests", null);

        var notices = TeamNotices.Compose("w1", board, bus);

        notices.ShouldNotBeNull();
        notices.ShouldStartWith(TeamNotices.Header);
        notices.ShouldContain("DM from w2: Leave Lexer.cs to me");
        notices.ShouldContain("G1 [FAIL] w2: Regex lexer too slow");
        notices.ShouldNotContain("Lexer has 12 tests");
        TeamNotices.Compose("w1", board, bus).ShouldBeNull();
    }
}
