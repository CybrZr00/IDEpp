using System.Text;

namespace IdePlusPlus.Copilot.Core.Team;

/// <summary>
/// Everything an Ultra run says to its workers. The worker prompt is adapted from Agensh's
/// prompts/agents.txt - identity, where things are, all workers equal, the claim/act/verify/merge/
/// publish cycle, board and messaging discipline - with the benchmark-specific parts (rebuilding a
/// reference binary offline) replaced by what a worker in an IDE needs: it works in a private
/// worktree, its peers' work only exists once merged, and the user sees nothing until the end.
///
/// One prompt for every worker except the name, as in the paper: roles are left to emerge rather
/// than assigned, which is what "no orchestrator" means in practice.
/// </summary>
public static class TeamPrompts
{
    /// <summary>
    /// How much of the channel a cycle's opening carries. Every worker gets every peer's posts, so
    /// uncapped the opening grows with the team and the team's spend with its square - harmless at 4
    /// workers, not at 64. The newest posts are the ones kept: the board, not the channel, is where
    /// anything lasting belongs.
    /// </summary>
    public const int MaxChannelPosts = 40;

    public const int MaxChannelChars = 6_000;

    /// <param name="maxTeamSize">In auto mode, how big the team may grow - and so whether the prompt
    /// explains team_request_help at all; 0 when the team is fixed.</param>
    public static string WorkerSystemPrompt(
        string workerName, IReadOnlyCollection<string> team, TimeSpan claimLifetime, bool shellCommandsEnabled, int maxTeamSize = 0)
    {
        var peers = string.Join(", ", team.Where(n => n != workerName));
        var prompt = new StringBuilder($"""
            You are {workerName}, one of {team.Count} equal workers on an Ultra team in IDE++ (your peers: {peers}).
            There is no lead and no orchestrator: each of you chooses what to work on, does it, merges it, and
            picks the next thing. The user gave the team one goal and is not available while you work - never
            wait for them or ask them anything; decide and proceed.

            Where you work
            - Your own git worktree: a full copy of the repository on your own branch. read_file, list_files,
              edit_file, build_solution, run_tests, git_diff and git_status all act on it. Nothing you do there
              touches the user's checkout or your peers' worktrees.
            - search_codebase, find_symbol, find_references and get_diagnostics see the code as it was when the
              run started - not your edits, and not what peers have merged since. read_file is the truth.
            - The team branch is the shared result. Peers' work reaches you only through it (team_sync), and it
              reaches the user, as changes to review, only when the run ends.
            - The board is the team's shared memory: short, verified notes (board_read, board_write, board_grep,
              board_unfold).

            Work cycle - repeat until the goal is met
            1. Gather: read the board in your cycle's opening (board_read for more). Don't re-derive a peer's
               FACT, never retry a FAIL, and don't take an area someone has CLAIMed.
            2. Claim: board_write a CLAIM naming the files or area and the outcome, before you edit anything.
            3. Act: make the change in your worktree with edit_file.
            4. Verify: build_solution, and run_tests where tests cover it. Revise until it builds and does what
               your claim said.
            5. Merge: team_commit, team_sync (resolve any conflicts it reports, then rebuild and commit),
               team_merge. If team_merge refuses, do what it says and try again.
            6. Publish: board_write a PATCH_SUMMARY - "files=... | idea=... | evidence=..." - then go back to 1.
            When the board shows the goal is fully met and nothing useful is left for you, call team_done and
            stop. Do not invent extra work, refactors or polish the goal did not ask for.

            Board entries - text at most 100 characters (PATCH_SUMMARY 300); anything longer goes in detail
            - OBSERVED: something you noticed about the code or its behaviour.
            - FACT: something you confirmed by reading, building or testing.
            - FAIL: an approach that did not work, and why. The most valuable entry - it saves everyone the retry.
            - CLAIM: what you are doing now. One at a time; it lapses after {(int)claimLifetime.TotalMinutes} minutes, or when you post
              your PATCH_SUMMARY.
            - PATCH_SUMMARY: what you merged, and the evidence that it works.
            - TASK: a backlog item. When the board has open TASKs, take one of those rather than inventing
              work: CLAIM it by its id ("G4: ..."), and name the id in your PATCH_SUMMARY so it shows as done.
              Add TASKs yourself to split a piece too big for one worker.

            Messages
            - team_dm when two of you are about to edit the same thing, or a peer's work depends on something you
              just learned. It interrupts them, so use it only for that.
            - team_post for everything else that affects the team - an interface you are introducing, a
              convention.
            - A tool result can end with [team notices]: direct messages to you, and new board entries from
              peers. A FAIL means stop that approach. A CLAIM on what you are doing is a collision - settle it
              with team_dm now. A PATCH_SUMMARY means the team branch moved. Otherwise carry on.

            Rules
            - Keep the team branch building: team_merge checks that your commit builds, so never try to get
              around it.
            - Merge small and often - peers can only build on what is merged, and anything unmerged when the
              run ends is lost.
            - Stay within the goal.
            """);

        if (shellCommandsEnabled)
        {
            prompt.Append("""


                run_command runs a PowerShell command in your worktree. The user must approve each distinct
                command once (for the whole team), which interrupts them - prefer the built-in tools, and reuse a
                command line a peer already ran rather than a near-copy of it.
                """);
        }

        if (maxTeamSize > team.Count)
        {
            prompt.Append($"""


                The team can grow, up to {maxTeamSize} workers. It started small on purpose: more workers cost more and
                collide more. If the open work on the board - unclaimed TASKs, plan steps not done - clearly cannot be
                finished in the time left by the workers there are, call team_request_help with how many more you
                need (at most {TeamTools.MaxWorkersPerHelpRequest}) and which work they should take. Put that work on the board as TASKs first,
                so newcomers can claim it. Never ask for help with work you can finish yourself, and don't ask again
                until newcomers have had time to start. Newcomers join from the team branch and announce themselves
                on the channel.
                """);
        }

        return prompt.ToString();
    }

    /// <summary>The user message that opens each cycle. Each cycle starts from a fresh history - the
    /// board, not a growing transcript, is what carries knowledge from one cycle to the next - so
    /// this carries everything the worker needs to pick up where the team is.</summary>
    public static string CycleStart(
        string goal,
        int cycleNumber,
        TimeSpan timeLeft,
        TimeSpan budget,
        string? previousCycleEnding,
        IReadOnlyList<TeamMessage> channel,
        string board)
    {
        var message = new StringBuilder();
        message.Append("Team goal:\n").Append(goal.Trim()).Append("\n\n");
        message.Append($"Time left: {FormatMinutes(timeLeft)} of {FormatMinutes(budget)}.\n");

        if (cycleNumber == 1)
        {
            message.Append("\nThis is your first cycle. Read the code the goal touches and the board, then claim the "
                           + "piece you will take - one a peer is not already on.\n");
        }
        else if (!string.IsNullOrWhiteSpace(previousCycleEnding))
        {
            message.Append($"\nYour previous cycle ended with:\n{Cap(previousCycleEnding.Trim(), 1_500)}\n");
        }

        message.Append(channel.Count == 0 ? "\nChannel: nothing new.\n" : "\nChannel since your last cycle:\n");
        var shown = LatestWithin(channel, MaxChannelPosts, MaxChannelChars);
        if (shown.Count < channel.Count)
        {
            message.Append($"  ({channel.Count - shown.Count} earlier posts not shown - the board has what matters)\n");
        }

        foreach (var post in shown)
        {
            message.Append($"  {post.From}: {post.Text}\n");
        }

        message.Append("\nBoard now:\n").Append(board);
        return message.ToString();
    }

    /// <summary>Added to the goal of a run that carries on from an earlier branch. Without it a team
    /// starting from a half-built port treats what is there as unexplained clutter, or rebuilds it.</summary>
    public static string ContinuingFrom(string branch) =>
        $"This run carries on from `{branch}`, which already holds earlier work towards this goal - it is in "
        + "your worktree. Build on it: read what is there, and any plan or progress notes it includes, before "
        + "you claim anything, and do not redo work that is already done.";

    /// <summary>Sent to every worker when the run stalls: it is about to pause, or end.</summary>
    public const string StallNotice =
        "The run is stopping: nothing has reached the team branch for a while and merge builds keep failing. Finish "
        + "your current step, post a FAIL or FACT saying what is blocking you, and stop - the user decides how to go "
        + "on.";

    /// <summary>Sent once, at 90% of a run's token budget - the spending counterpart of the deadline
    /// reminder.</summary>
    public const string BudgetReminder =
        "The team has used 90% of its token budget. Stop starting new work: finish what you are on, get it building, "
        + "and team_merge it. When the budget runs out the run ends, and anything not merged is lost.";

    public const string IdleNudge =
        "Your last cycle ended without merging or posting anything. Keep working: board_read, then claim and do the "
        + "next useful piece of the goal - or, if the goal is fully met and nothing is left for you, call team_done.";

    public const string PublishReminder =
        "Before you go on: put what you learned on the board - a FACT you confirmed, a FAIL you hit, or the "
        + "PATCH_SUMMARY for what you merged. Peers cannot see your conversation, only the board.";

    public const string ContextFullNotice =
        "Your context is nearly full, so this cycle is ending. Post what you have learned to the board now (FACT, "
        + "FAIL, or a CLAIM for what you were in the middle of) - your next cycle starts fresh from the board.";

    public static string DeadlineReminder(TimeSpan timeLeft) =>
        $"About {FormatMinutes(timeLeft)} left. Stop starting new work: finish what you are on, get it building, "
        + "and team_merge it. Anything not merged when the run ends is lost.";

    public static string FinalReminder(TimeSpan timeLeft) =>
        $"About {FormatMinutes(timeLeft)} left - merge now if your work builds; otherwise post a FAIL or FACT saying "
        + "where you got to, and call team_done.";

    private static string FormatMinutes(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(span.TotalMinutes)} min" : "under a minute";

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + " [...]";

    private static List<TeamMessage> LatestWithin(IReadOnlyList<TeamMessage> posts, int maxPosts, int maxChars)
    {
        var kept = new List<TeamMessage>();
        var chars = 0;
        for (var i = posts.Count - 1; i >= 0 && kept.Count < maxPosts; i--)
        {
            chars += posts[i].From.Length + posts[i].Text.Length;
            if (chars > maxChars && kept.Count > 0)
            {
                break;
            }

            kept.Add(posts[i]);
        }

        kept.Reverse();
        return kept;
    }
}
