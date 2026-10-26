using IdePlusPlus.Copilot.Core.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TeamPromptsTests
{
    /// <summary>Every worker gets every peer's posts, so on a big team an uncapped channel would make
    /// each cycle's opening - and the team's spend - grow with the square of its size.</summary>
    [Fact]
    public void CycleStart_KeepsOnlyTheNewestChannelPosts_AndSaysHowManyItLeftOut()
    {
        var channel = Enumerable.Range(1, 100)
            .Select(i => new TeamMessage($"w{i % 60 + 1}", null, $"post {i}", DateTimeOffset.UnixEpoch))
            .ToList();

        var opening = TeamPrompts.CycleStart("Goal.", 2, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), null, channel, "Board.");

        opening.ShouldContain($"({100 - TeamPrompts.MaxChannelPosts} earlier posts not shown");
        opening.ShouldContain(": post 100\n");
        opening.ShouldContain($": post {100 - TeamPrompts.MaxChannelPosts + 1}\n");
        opening.ShouldNotContain($": post {100 - TeamPrompts.MaxChannelPosts}\n");
    }

    [Fact]
    public void CycleStart_CapsTheChannelByLength_ButAlwaysShowsTheNewestPost()
    {
        var huge = new string('x', TeamPrompts.MaxChannelChars);
        TeamMessage[] channel = [new("w1", null, "older " + huge, DateTimeOffset.UnixEpoch), new("w2", null, "newest " + huge, DateTimeOffset.UnixEpoch)];

        var opening = TeamPrompts.CycleStart("Goal.", 2, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), null, channel, "Board.");

        opening.ShouldContain("(1 earlier posts not shown");
        opening.ShouldContain("w2: newest");
        opening.ShouldNotContain("w1: older");
    }
}
