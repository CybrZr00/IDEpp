using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public class TeamReviewQueueTests
{
    private static readonly TimeSpan NoWait = TimeSpan.FromMilliseconds(1);

    private static MergeLanding Landing(int generation = 1, string author = "b1", string? taskId = "G4") =>
        new("aaa111", "bbb222", author, ["src/Foo/Bar.cs"], generation, taskId, "files=src/Foo/Bar.cs | idea=x | evidence=build ok");

    private static TeamReviewQueue BalancedQueue() =>
        new([TeamRoleKind.MutationTester, TeamRoleKind.Adversary, TeamRoleKind.Conformity, TeamRoleKind.GapFinder]);

    [Fact]
    public void Enqueue_QueuesTheMergeForEveryLaneThatWorksOnDiffs()
    {
        var queue = BalancedQueue();

        var item = queue.Enqueue(Landing());

        item.ShouldNotBeNull();
        item!.Lanes.ShouldBe([TeamRoleKind.MutationTester, TeamRoleKind.Adversary, TeamRoleKind.Conformity]);
        item.Lanes.ShouldAllBe(lane => item.StatusFor(lane) == ReviewItemStatus.Pending);
        item.FromSha.ShouldBe("aaa111");
        item.ToSha.ShouldBe("bbb222");
        item.Author.ShouldBe("b1");
        item.TaskId.ShouldBe("G4");
        item.Files.ShouldBe(["src/Foo/Bar.cs"]);
        queue.Drained.ShouldBeFalse();
    }

    [Fact]
    public void TakeNext_GivesTheOldestItemForTheLane_AndLeavesOtherLanesTheirOwn()
    {
        var queue = BalancedQueue();
        var first = queue.Enqueue(Landing(taskId: "G4"))!;
        var second = queue.Enqueue(Landing(taskId: "G5"))!;

        queue.TakeNext(TeamRoleKind.Adversary).ShouldBeSameAs(first);
        queue.TakeNext(TeamRoleKind.Adversary).ShouldBeSameAs(second);
        queue.TakeNext(TeamRoleKind.Adversary).ShouldBeNull();

        first.StatusFor(TeamRoleKind.Adversary).ShouldBe(ReviewItemStatus.Taken);
        first.StatusFor(TeamRoleKind.MutationTester).ShouldBe(ReviewItemStatus.Pending);
        queue.OutstandingFor(TeamRoleKind.MutationTester).ShouldBe(2);
        queue.TakeNext(TeamRoleKind.MutationTester).ShouldBeSameAs(first);
    }

    [Fact]
    public void ACompositionWithNoSpecialists_QueuesNothing_WhichIsTheSpeedPreset()
    {
        var queue = new TeamReviewQueue(TeamComposition.Speed(4).Specialists.Keys);

        queue.Enqueue(Landing()).ShouldBeNull();
        queue.Drained.ShouldBeTrue();
        queue.MergeCount.ShouldBe(1);
    }

    [Fact]
    public void Completion_MarksTheLanesPartDone_AndTheQueueIsDrainedOnlyWhenEveryLaneIsDone()
    {
        var queue = new TeamReviewQueue([TeamRoleKind.MutationTester, TeamRoleKind.Adversary]);
        var item = queue.Enqueue(Landing())!;

        var mutation = queue.TakeNext(TeamRoleKind.MutationTester)!;
        queue.Complete(mutation, TeamRoleKind.MutationTester);
        item.StatusFor(TeamRoleKind.MutationTester).ShouldBe(ReviewItemStatus.Done);
        queue.Drained.ShouldBeFalse();

        var adversary = queue.TakeNext(TeamRoleKind.Adversary)!;
        queue.Complete(adversary, TeamRoleKind.Adversary);
        queue.Drained.ShouldBeTrue();
        queue.OutstandingFor(TeamRoleKind.Adversary).ShouldBe(0);
    }

    [Fact]
    public void LoopGuard_AnItemAtTheSecondGeneration_GoesOnlyToTheConformityLane()
    {
        var queue = BalancedQueue();

        var fix = queue.Enqueue(Landing(generation: TeamReviewQueue.LoopGuardGeneration));

        fix.ShouldNotBeNull();
        fix!.Lanes.ShouldBe([TeamRoleKind.Conformity]);

        // A run without a conformity lane has nobody to give it to, so it queues nothing rather than
        // leaving an item no lane can ever finish.
        var mutationsOnly = new TeamReviewQueue([TeamRoleKind.MutationTester, TeamRoleKind.Adversary]);
        mutationsOnly.Enqueue(Landing(generation: 3)).ShouldBeNull();
        mutationsOnly.Drained.ShouldBeTrue();
    }

    [Fact]
    public void TheGoalLevelLanes_GetAnItemEveryFewMerges_NotOnePerMerge()
    {
        var queue = BalancedQueue();

        queue.Enqueue(Landing());
        queue.Enqueue(Landing());
        queue.Items.ShouldAllBe(item => !item.Lanes.Contains(TeamRoleKind.GapFinder));

        queue.Enqueue(Landing());
        queue.Items.Count(item => item.Lanes.Contains(TeamRoleKind.GapFinder))
            .ShouldBe(1);
        queue.Items.Single(item => item.Lanes.Contains(TeamRoleKind.GapFinder)).Id.ShouldBe(3);
    }

    [Fact]
    public void EnqueuePeriodic_QueuesTheWrapUpLook_ForTheGoalLevelLanesOnly()
    {
        var queue = BalancedQueue();

        var item = queue.EnqueuePeriodic("ccc333", note: "wrap-up");

        item.ShouldNotBeNull();
        item!.IsPeriodic.ShouldBeTrue();
        item.Lanes.ShouldBe([TeamRoleKind.GapFinder]);
        queue.TakeNext(TeamRoleKind.MutationTester).ShouldBeNull();
    }

    [Fact]
    public void Close_StopsTakingNewItems_ButLeavesTheQueuedOnesTakeable()
    {
        var queue = BalancedQueue();
        var queued = queue.Enqueue(Landing())!;

        queue.Close();

        queue.IsClosed.ShouldBeTrue();
        queue.Enqueue(Landing()).ShouldBeNull();
        queue.Items.Count.ShouldBe(1);
        queue.TakeNext(TeamRoleKind.MutationTester).ShouldBeSameAs(queued);

        queue.Open();
        queue.Enqueue(Landing()).ShouldNotBeNull();
    }

    [Fact]
    public async Task WaitForItemAsync_ReturnsTheItemAwaited_WithoutPollingTheModel()
    {
        var queue = new TeamReviewQueue([TeamRoleKind.Adversary]);

        var waiting = queue.WaitForItemAsync(TeamRoleKind.Adversary, TimeSpan.FromSeconds(10), CancellationToken.None);
        queue.TakeNext(TeamRoleKind.Adversary).ShouldBeNull();

        var queued = queue.Enqueue(Landing())!;
        (await waiting).ShouldBeSameAs(queued);
    }

    [Fact]
    public async Task WaitForItemAsync_GivesUpWhenTheWaitRunsOut_AndWhenTheQueueCloses()
    {
        var queue = new TeamReviewQueue([TeamRoleKind.Adversary]);

        (await queue.WaitForItemAsync(TeamRoleKind.Adversary, NoWait, CancellationToken.None))
            .ShouldBeNull();

        var waiting = queue.WaitForItemAsync(TeamRoleKind.Adversary, TimeSpan.FromSeconds(10), CancellationToken.None);
        queue.Close();
        (await waiting).ShouldBeNull();
    }

    [Fact]
    public async Task WaitForItemAsync_DoesNotHandOutAnItemAnotherLaneTook()
    {
        var queue = new TeamReviewQueue([TeamRoleKind.MutationTester, TeamRoleKind.Adversary]);
        var item = queue.Enqueue(Landing())!;
        queue.TakeNext(TeamRoleKind.MutationTester).ShouldBeSameAs(item);

        (await queue.WaitForItemAsync(TeamRoleKind.Adversary, TimeSpan.FromSeconds(10), CancellationToken.None))
            .ShouldBeSameAs(item);
        item.StatusFor(TeamRoleKind.MutationTester).ShouldBe(ReviewItemStatus.Taken);
    }

    [Fact]
    public void Describe_SaysWhatEachLaneHasLeft()
    {
        var queue = new TeamReviewQueue([TeamRoleKind.Adversary]);
        queue.Enqueue(Landing());

        var description = queue.Describe();

        description.ShouldContain("Adversary 1 pending");
        description.ShouldContain("0 done");
    }
}
