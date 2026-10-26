using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.SourceControl;
using Serilog;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Ultra effort: a message sent at Ultra goes to an Agensh-style team (see UltraTeamRun) instead of
/// the single agent, and what the team merged comes back as edit proposals to review. In its own
/// file for the same reason as the template tools - it shares little with the turn loop beyond the
/// turn scope and the solution context.
/// </summary>
public sealed partial class CopilotChatPaneViewModel
{
    public const string UltraWorkerCountSettingKey = "UltraWorkerCount";
    public const string UltraTimeBudgetSettingKey = "UltraTimeBudgetMinutes";
    public const string UltraConcurrentRequestsSettingKey = "UltraMaxConcurrentRequests";
    public const string UltraRunUntilDoneSettingKey = "UltraRunUntilDone";
    public const string UltraMaxRunMinutesSettingKey = "UltraMaxRunMinutes";
    public const string UltraMaxTokensMillionsSettingKey = "UltraMaxTokensMillions";
    public const int DefaultUltraWorkerCount = 4;
    public const int MaxUltraWorkerCount = 64;
    public const int DefaultUltraConcurrentRequests = 16;
    public const int DefaultUltraMaxRunMinutes = 480;
    public const int MaxUltraMaxRunMinutes = 2_880;
    public const int DefaultUltraMaxTokensMillions = 50;
    public const int MaxUltraMaxTokensMillions = 2_000;
    public const int DefaultUltraTimeBudgetMinutes = 30;
    public const int MinUltraTimeBudgetMinutes = 5;
    public const int MaxUltraTimeBudgetMinutes = 240;

    /// <summary>How much of the conversation before an Ultra message the team is given. Workers start
    /// with no memory of it, so "do what we discussed" needs the discussion - but only the gist.</summary>
    private const int MaxTeamGoalContextChars = 4_000;

    private const int MaxTeamGoalMessageChars = 600;

    private readonly TeamGitWorkspace _teamGit = new();

    /// <summary>One run_command approval prompt at a time across all workers, so a second worker
    /// asking for the same command finds it already approved instead of opening a second dialog.</summary>
    private readonly SemaphoreSlim _teamShellApprovals = new(1, 1);

    /// <summary>Where run folders (worktrees while running, board.jsonl after) go. Outside every
    /// repository, so a worktree never shows up in the user's git status, file list or index.</summary>
    internal string UltraRootDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdePlusPlus", "ultra");

    /// <summary>
    /// Asked before an Ultra worker's run_command, with the exact command line and the worker's
    /// reason. Unlike ShellCommandApprovalPrompt it can answer for the rest of the run
    /// (<see cref="ApprovalChoice.AllowAll"/>). Unset, team runs fall back to ShellCommandApprovalPrompt.
    /// </summary>
    public Func<string, string, Task<ApprovalChoice>>? TeamShellCommandApprovalPrompt { get; set; }

    /// <summary>
    /// Asked before an Ultra run carries on from a team branch that a later run already carried on
    /// from - (branch chosen, later branch, commits of work the later one has on top). Starting from
    /// the chosen one leaves that work out, and the team would likely do it all again. Unset, the run
    /// starts from the branch chosen without asking.
    /// </summary>
    public Func<string, string, int, UltraStartChoice>? LaterTeamBranchPrompt { get; set; }

    /// <summary>The "Start from" choice that means no branch: the run starts from the working tree alone.</summary>
    public const string WorkingTreeStartOption = "(your working tree)";

    /// <summary>What an Ultra run can carry on from: the working tree, then this repository's team
    /// branches from earlier runs, newest first. The combo box is editable, so any other branch or
    /// commit can be typed in.</summary>
    public ObservableCollection<string> UltraStartOptions { get; } = [WorkingTreeStartOption];

    [ObservableProperty]
    private string ultraStartFrom = WorkingTreeStartOption;

    private string? SelectedUltraStartBranch =>
        string.IsNullOrWhiteSpace(UltraStartFrom) || UltraStartFrom == WorkingTreeStartOption ? null : UltraStartFrom.Trim();

    /// <summary>
    /// Re-reads the team branches on offer. A failure (no repository, git missing) just leaves the
    /// working tree as the only option - the picker is a convenience, and a run outside git falls back
    /// to a single agent anyway. A selection that no longer exists is reset rather than left to fail
    /// at send time; one the user typed that was never on the list is left alone.
    /// </summary>
    internal async Task RefreshUltraStartOptionsAsync()
    {
        IReadOnlyList<string> branches = [];
        try
        {
            if (_solutionDirectory is { } directory
                && await _teamGit.FindRepositoryRootAsync(directory) is { } repositoryRoot)
            {
                branches = await _teamGit.ListTeamBranchesAsync(repositoryRoot);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log.Warning(ex, "Couldn't list the Ultra team branches to carry on from");
        }

        var selected = UltraStartFrom;
        var wasOffered = UltraStartOptions.Contains(selected);
        UltraStartOptions.Clear();
        UltraStartOptions.Add(WorkingTreeStartOption);
        foreach (var branch in branches)
        {
            UltraStartOptions.Add(branch);
        }

        UltraStartFrom = wasOffered && !UltraStartOptions.Contains(selected) ? WorkingTreeStartOption : selected;
    }

    /// <summary>Drives the pane's Ultra banner, which says what choosing it means before anything runs.</summary>
    public bool IsUltraEffort => SelectedEffort == ChatEffort.Ultra;

    public static int ReadUltraWorkerCount(AppSettingsStore settings) =>
        int.TryParse(settings.Get(UltraWorkerCountSettingKey), out var count)
            ? Math.Clamp(count, 1, MaxUltraWorkerCount)
            : DefaultUltraWorkerCount;

    /// <summary>How many model requests a team may have in flight at once - its throttle on the
    /// provider, however many workers it has.</summary>
    public static int ReadUltraConcurrentRequests(AppSettingsStore settings) =>
        int.TryParse(settings.Get(UltraConcurrentRequestsSettingKey), out var requests)
            ? Math.Clamp(requests, 1, MaxUltraWorkerCount)
            : DefaultUltraConcurrentRequests;

    /// <summary>The longest a run left to go until done may take - its only time limit, since there
    /// is nobody to ask at the end of a round.</summary>
    public static int ReadUltraMaxRunMinutes(AppSettingsStore settings) =>
        int.TryParse(settings.Get(UltraMaxRunMinutesSettingKey), out var minutes)
            ? Math.Clamp(minutes, MinUltraTimeBudgetMinutes, MaxUltraMaxRunMinutes)
            : DefaultUltraMaxRunMinutes;

    /// <summary>The token budget, in millions, a run left to go until done ends at; 0 means none.</summary>
    public static int ReadUltraMaxTokensMillions(AppSettingsStore settings) =>
        int.TryParse(settings.Get(UltraMaxTokensMillionsSettingKey), out var millions)
            ? Math.Clamp(millions, 0, MaxUltraMaxTokensMillions)
            : DefaultUltraMaxTokensMillions;

    /// <summary>
    /// Set, an Ultra message runs the team until it is done - every worker reports done - or a cap in
    /// Settings is reached: total time, total tokens, or a stall. Nobody is asked at the end of a
    /// round, and what reached the team branch is then landed on the checked-out branch as a commit
    /// instead of offered file by file. Saved as soon as it changes, like Auto Mode.
    /// </summary>
    [ObservableProperty]
    private bool isUltraRunUntilDone;

    partial void OnIsUltraRunUntilDoneChanged(bool value) => _appSettingsStore.Set(UltraRunUntilDoneSettingKey, value.ToString());

    public static int ReadUltraTimeBudgetMinutes(AppSettingsStore settings) =>
        int.TryParse(settings.Get(UltraTimeBudgetSettingKey), out var minutes)
            ? Math.Clamp(minutes, MinUltraTimeBudgetMinutes, MaxUltraTimeBudgetMinutes)
            : DefaultUltraTimeBudgetMinutes;

    /// <summary>
    /// The team's goal: the request itself, plus the last few plain messages before it. Workers start
    /// with nothing but this and the code, so a request that leans on the conversation ("now do the
    /// same for the other two") needs that conversation, capped so it cannot crowd out the goal.
    /// </summary>
    internal static string ComposeTeamGoal(string request, IEnumerable<ObservableObject> earlierMessages)
    {
        var context = new List<string>();
        var total = 0;
        foreach (var message in earlierMessages.OfType<ChatMessageViewModel>().Reverse())
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var text = message.Text.Trim();
            if (text.Length > MaxTeamGoalMessageChars)
            {
                text = text[..MaxTeamGoalMessageChars] + " [...]";
            }

            var line = $"{(message.IsUser ? "User" : "Assistant")}: {text}";
            if (total + line.Length > MaxTeamGoalContextChars)
            {
                break;
            }

            context.Insert(0, line);
            total += line.Length;
        }

        return context.Count == 0
            ? request
            : $"{request}\n\nEarlier in the conversation (for context only - the request above is the goal):\n"
              + string.Join("\n", context);
    }

    /// <summary>
    /// Runs the team on <paramref name="goal"/>, or - when the solution is not in a git repository,
    /// which a team needs for its worktrees - says why and answers as a single High-effort agent
    /// instead, through <paramref name="fallbackHistory"/>.
    /// </summary>
    /// <param name="backlog">Steps of an approved plan, put on the board as TASKs for workers to take.</param>
    private async Task RunUltraTeamAsync(
        string goal,
        List<ChatMessage> fallbackHistory,
        ChatMessageViewModel assistantMessage,
        string? fallbackContextQuery,
        IReadOnlyList<string>? backlog = null)
    {
        string? fallbackReason = null;
        var turn = BeginTurn("Starting the Ultra team...");
        TeamRunViewModel? card = null;
        try
        {
            var repositoryRoot = _solutionDirectory is null
                ? null
                : await _teamGit.FindRepositoryRootAsync(_solutionDirectory, turn.Token);
            ThrowIfDetached(turn);

            if (_solutionDirectory is null || _solutionPath is null)
            {
                fallbackReason = "Ultra needs an open solution";
            }
            else if (repositoryRoot is null)
            {
                fallbackReason = "Ultra needs the solution to be in a git repository - each worker gets its own worktree";
            }
            else
            {
                // The snapshot the team starts from is what is on disk, so unsaved editor changes are
                // saved first - the same as build_solution does, for the same reason.
                _editApplicationService.SaveDirtyDocuments();

                // Before the run exists, since creating one makes its folder. The picker lists every
                // team branch newest first, but one run carrying on from another leaves both listed,
                // and picking the older is easy - and silently drops everything the later run did.
                var startFrom = SelectedUltraStartBranch;
                if (startFrom is not null
                    && LaterTeamBranchPrompt is { } askAboutLaterBranch
                    && await _teamGit.FindLaterTeamBranchAsync(repositoryRoot, startFrom, turn.Token) is { } later)
                {
                    ThrowIfDetached(turn);
                    switch (askAboutLaterBranch(startFrom, later.Branch, later.LaterCommits))
                    {
                        case UltraStartChoice.StartFromLater:
                            startFrom = later.Branch;
                            UltraStartFrom = later.Branch;
                            break;
                        case UltraStartChoice.Cancel:
                            assistantMessage.Text = $"**Ultra team** - not started. `{later.Branch}` already carries on from "
                                                    + $"`{startFrom}`. Choose under Start from, then send again.";
                            return;
                    }
                }

                // Until done, the caps are the only limits: one round as long as the time cap, and
                // nobody asked to continue - a stall or a spent budget ends the run instead.
                var runUntilDone = IsUltraRunUntilDone;
                var options = new UltraTeamOptions(
                    ReadUltraWorkerCount(_appSettingsStore),
                    TimeSpan.FromMinutes(runUntilDone ? ReadUltraMaxRunMinutes(_appSettingsStore) : ReadUltraTimeBudgetMinutes(_appSettingsStore)),
                    AreShellCommandsEnabled,
                    _currentConfiguration?.Invoke() ?? "Debug",
                    ChatEffort.Ultra)
                {
                    MaxConcurrentModelRequests = ReadUltraConcurrentRequests(_appSettingsStore),
                    MaxTokens = runUntilDone ? ReadUltraMaxTokensMillions(_appSettingsStore) * 1_000_000L : 0,
                };
                IChatClient client = CurrentChatClient;
                var teamApprovals = new TeamShellApprovals();
                TeamRunViewModel? runCard = null;
                var run = new UltraTeamRun(
                    client, SelectedModel, options, _readOnlyToolDispatcher, _gitService, _teamGit,
                    TimeProvider.System, UltraRootDirectory,
                    (command, reason, directory, ct) => RunTeamShellCommandAsync(command, reason, directory, teamApprovals, ct),
                    askToContinue: runUntilDone ? null : ct => _dispatcher.InvokeAsync(async () =>
                    {
                        StatusMessage = "The Ultra team is waiting for you - keep it going, or finish and review? (on the team card)";
                        try
                        {
                            return await runCard!.AskToContinueAsync(options.TimeBudget, ct);
                        }
                        finally
                        {
                            StatusMessage = null;
                        }
                    }).Task.Unwrap());

                card = new TeamRunViewModel(
                    run, SelectedProvider == ProviderLmStudio, branches => DeleteRunBranchesAsync(repositoryRoot, branches), startFrom, runUntilDone);
                runCard = card;
                teamApprovals.AllowAllGranted = () => runCard.ShellNote = "Shell: every command allowed for this run.";
                Messages.Insert(Messages.IndexOf(assistantMessage), card);
                CurrentActivity = $"Ultra team working - {options.WorkerCount} workers, up to {options.TimeBudget.TotalMinutes:0} min...";

                var solutionPath = _solutionPath;
                var testProjects = _testProjectPaths;
                // On the thread pool: the run's own continuations then stay off the UI thread rather
                // than queueing behind it for every tool call of every worker.
                var result = await Task.Run(
                    () => run.RunAsync(goal, repositoryRoot, solutionPath, testProjects, turn.Token, startFrom, backlog), turn.Token);
                ThrowIfDetached(turn);

                card.Finish(result);
                TotalPromptTokens += result.PromptTokens;
                TotalCompletionTokens += result.CompletionTokens;

                // Until done, the team's work is merged into the user's branch - there was nobody to
                // review it file by file. Anything that stops a clean merge (a conflict with commits
                // made since, local edits that differ, unsaved tabs) falls back to that review.
                TeamLandResult? landing = null;
                if (runUntilDone && result.TeamBranchKept)
                {
                    CurrentActivity = "Merging the team's work into your branch...";
                    var unsaved = _editApplicationService.GetUnsavedPaths();
                    landing = await _teamGit.LandOnCheckedOutBranchAsync(
                        repositoryRoot,
                        result.TeamBranch,
                        LandingCommitMessage(goal, run, result),
                        changed => UnsavedAmong(changed, repositoryRoot, unsaved),
                        turn.Token);
                    ThrowIfDetached(turn);
                }

                if (landing is { Status: not TeamLandStatus.Refused })
                {
                    _editApplicationService.ReloadFromDisk(
                        (landing.ChangedPaths ?? []).Select(p => Path.GetFullPath(Path.Combine(repositoryRoot, p))));
                    assistantMessage.Text = DescribeTeamResult(run, result, 0, [], landing);
                }
                else
                {
                    CurrentActivity = "Preparing the team's changes for review...";
                    await ProposeTeamResultAsync(
                        run, result, assistantMessage, turn.Token,
                        landing is null ? null : $"Couldn't merge into your branch: {landing.Message}. Offered for review instead.");
                }

                await RefreshUltraStartOptionsAsync();
            }
        }
        catch (OperationCanceledException) when (turn.Token.IsCancellationRequested || !IsLive(turn))
        {
            if (card is { IsRunning: true })
            {
                card.Abandon("Stopped.");
            }

            assistantMessage.Append("\n\n[cancelled]");
        }
        catch (UltraTeamPreflightException ex)
        {
            card?.Abandon("Didn't start - the solution doesn't build.");
            assistantMessage.Text = $"**Ultra team** - didn't start. {ex.Message} No workers were started and "
                                    + $"nothing was spent on them.\n\n```\n{ex.Report}\n```";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ultra team run failed");
            if (card is { IsRunning: true })
            {
                card.Abandon($"Failed: {ex.Message}");
            }

            assistantMessage.Append($"\n\n[error: the Ultra team stopped unexpectedly - {ex.Message}]");
            if (IsLive(turn))
            {
                StatusMessage = "The Ultra team stopped due to an unexpected error - see the message above.";
            }
        }
        finally
        {
            var wasLive = IsLive(turn);
            EndTurn(turn);
            if (wasLive && fallbackReason is null)
            {
                PersistChatHistory();
            }
        }

        // Only set on a turn that was still live when it decided, and EndTurn has since released it,
        // so starting the fallback turn here cannot trample another one.
        if (fallbackReason is not null)
        {
            StatusMessage = $"{fallbackReason}, so this was answered by a single agent at High effort.";
            await RunAssistantTurnsAsync(fallbackHistory, assistantMessage, fallbackContextQuery);
        }
    }

    /// <summary>
    /// Turns what reached the team branch into one edit proposal per file, so landing it is the same
    /// review the user already does for any edit - nothing is written, staged or committed until they
    /// accept. A file the user changed while the team worked is 3-way merged (their copy, the
    /// snapshot the team started from, the team's version) rather than overwritten. Whatever a
    /// proposal cannot carry - deletions, binary files, files outside the solution folder - is listed
    /// for the user to take from the team branch themselves.
    /// </summary>
    /// <param name="notLandedNote">Why a run left to go until done could not be merged into the
    /// user's branch, put at the top of the summary - null for an ordinary run.</param>
    private async Task ProposeTeamResultAsync(
        UltraTeamRun run,
        UltraTeamResult result,
        ChatMessageViewModel assistantMessage,
        CancellationToken cancellationToken,
        string? notLandedNote = null)
    {
        var solutionDirectory = _solutionDirectory!;
        var notProposed = new List<string>();
        var proposals = new List<(EditOperation Operation, string Note)>();

        foreach (var change in result.Changes)
        {
            var fullPath = Path.GetFullPath(Path.Combine(result.RepositoryRoot, change.RelativePath));
            var relativeToSolution = Path.GetRelativePath(solutionDirectory, fullPath);
            if (relativeToSolution.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToSolution))
            {
                notProposed.Add($"`{change.RelativePath}` - outside the solution folder");
                continue;
            }

            if (change.Kind == TeamFileChangeKind.Deleted)
            {
                notProposed.Add($"`{change.RelativePath}` - deleted by the team; delete it yourself if you agree");
                continue;
            }

            var team = await _teamGit.GetFileAtAsync(result.RepositoryRoot, result.TeamBranch, change.RelativePath, cancellationToken);
            if (team is null)
            {
                continue;
            }

            if (team.Contains('\0'))
            {
                notProposed.Add($"`{change.RelativePath}` - a binary file");
                continue;
            }

            var exists = File.Exists(fullPath) || _editApplicationService.IsOpen(fullPath);
            var current = exists ? _editApplicationService.GetCurrentContent(fullPath) : null;
            var snapshot = change.Kind == TeamFileChangeKind.Added
                ? ""
                : await _teamGit.GetFileAtAsync(result.RepositoryRoot, result.SnapshotCommit, change.RelativePath, cancellationToken) ?? "";

            string content;
            string note;
            if (current is null)
            {
                if (change.Kind == TeamFileChangeKind.Modified)
                {
                    notProposed.Add($"`{change.RelativePath}` - changed by the team, but you deleted it since the run started");
                    continue;
                }

                content = team;
                note = "New file from the Ultra team run.";
            }
            else if (SameText(current, snapshot))
            {
                content = team;
                note = "From the Ultra team run.";
            }
            else
            {
                var (merged, hasConflicts) = await _teamGit.MergeFileContentsAsync(current, snapshot, team, cancellationToken);
                content = merged;
                note = hasConflicts
                    ? "From the Ultra team run, merged with edits you made while it ran - it has CONFLICTS: resolve "
                      + "the <<<<<<< / >>>>>>> markers after accepting, or reject it and merge from the team branch."
                    : "From the Ultra team run, merged with edits you made while it ran.";
            }

            if (current is not null && SameText(content, current))
            {
                continue;
            }

            proposals.Add((new EditOperation(relativeToSolution, "whole_file", null, null, content), note));
        }

        assistantMessage.Text = DescribeTeamResult(run, result, proposals.Count, notProposed, notLandedNote: notLandedNote);

        foreach (var (operation, note) in proposals)
        {
            // Reply-text proposals: Auto Mode never applies these, which is the point - the team's
            // work is reviewed by a person however Auto Mode is set.
            var proposal = new EditProposalViewModel($"ultra-{Guid.NewGuid():N}", _editApplicationService);
            AttachEditHooks(proposal);
            Messages.Add(proposal);
            proposal.CompleteFromReplyText(operation, solutionDirectory, note);
        }
    }

    /// <param name="landing">Set when the result was merged into the user's branch (or already in
    /// it) instead of offered for review.</param>
    private static string DescribeTeamResult(
        UltraTeamRun run,
        UltraTeamResult result,
        int proposalCount,
        IReadOnlyList<string> notProposed,
        TeamLandResult? landing = null,
        string? notLandedNote = null)
    {
        var text = new StringBuilder("**Ultra team** - ");
        text.Append(result.EndReason switch
        {
            UltraTeamEndReason.AllDone => "finished: every worker reported done.",
            UltraTeamEndReason.TimeUp => "stopped: the time budget ran out.",
            UltraTeamEndReason.Cancelled => "stopped.",
            UltraTeamEndReason.Stalled => "stopped: the team had stalled - nothing could merge, and merge builds kept failing.",
            UltraTeamEndReason.BudgetSpent => $"stopped: the token budget ({run.MaxTokens:N0}) was spent.",
            _ => "stopped: no worker was still making progress.",
        });
        text.Append($" {result.Workers.Count} workers, {(int)run.Elapsed.TotalMinutes} min"
                    + (run.Round > 1 ? $" over {run.Round} rounds" : "")
                    + $", {result.PromptTokens + result.CompletionTokens:N0} tokens"
                    + (run.CachedPromptTokens > 0 && result.PromptTokens > 0
                        ? $" ({100.0 * run.CachedPromptTokens / result.PromptTokens:0}% of input from the provider's cache)"
                        : "")
                    + ".\n\n");
        if (run.StartedFrom is not null)
        {
            text.Append($"Carried on from `{run.StartedFrom}`"
                        + (run.CarriedBoardEntries > 0 ? $", with {run.CarriedBoardEntries} board entries taken over from that run" : "")
                        + ".\n\n");
        }

        if (notLandedNote is not null)
        {
            text.Append($"{notLandedNote}\n\n");
        }

        if (result.Changes.Count == 0)
        {
            text.Append("Nothing was merged, so there is nothing to review.\n");
        }
        else if (landing is { Status: TeamLandStatus.Landed, Commit: { } commit })
        {
            text.Append($"Merged into `{landing.BranchName ?? "HEAD"}` as commit {commit[..Math.Min(9, commit.Length)]}"
                        + $" - {landing.ChangedPaths?.Count ?? 0} file(s) changed. The workers' own commits are in its history; "
                        + $"nothing is left to review. `{result.TeamBranch}` stays until you delete it.\n");
        }
        else if (landing is { Status: TeamLandStatus.NothingToLand })
        {
            text.Append($"Nothing to merge: {landing.Message}.\n");
        }
        else
        {
            text.Append(proposalCount > 0
                ? $"{proposalCount} file(s) to review below. Accept the ones you want - they land as uncommitted "
                  + "changes; nothing is committed. "
                : "No changes left to review. ");
            text.Append($"Everything the team merged is on branch `{result.TeamBranch}` until you delete it.\n");
        }

        var summaries = result.Board.Where(e => e.Type == TeamBoardEntryType.PatchSummary).ToList();
        if (summaries.Count > 0)
        {
            // Their own PATCH_SUMMARY entries: what each worker said it did, which on a run whose
            // merge gate kept refusing is not the same as what reached the team branch.
            text.Append("\nWhat the workers reported:\n");
            foreach (var entry in summaries)
            {
                text.Append($"- {entry.Author}: {entry.Text}\n");
            }
        }

        if (notProposed.Count > 0)
        {
            text.Append("\nNot offered for review - take these from the team branch yourself:\n");
            foreach (var line in notProposed)
            {
                text.Append($"- {line}\n");
            }
        }

        if (result.KeptWorkerBranches.Count > 0)
        {
            text.Append("\nWork that never reached the team branch is kept on these worker branches (nothing in "
                        + "them is offered for review above):\n");
            foreach (var branch in result.KeptWorkerBranches)
            {
                text.Append($"- `{branch}`\n");
            }
        }

        if (result.TeamBranchKept)
        {
            text.Append($"\nTo carry on from this run, choose `{result.TeamBranch}` under Start from before sending the "
                        + "next Ultra message.\n");
        }

        text.Append("\nWorkers:\n");
        foreach (var worker in result.Workers)
        {
            text.Append(worker.IsDone
                ? $"- {worker.Name}: done - {worker.DoneSummary}\n"
                : $"- {worker.Name}: stopped after {worker.Steps} steps\n");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The merge commit's message: the request, then what the run was - enough for someone
    /// reading the log later to know a team made this and where its full history is.</summary>
    internal static string LandingCommitMessage(string goal, UltraTeamRun run, UltraTeamResult result)
    {
        var request = goal.Split('\n', 2)[0].Trim();
        if (request.Length > 60)
        {
            request = request[..57].TrimEnd() + "...";
        }

        var message = new StringBuilder($"Merge Ultra run {result.RunId}: {request}\n\n");
        message.Append($"{result.Workers.Count} workers, {(int)run.Elapsed.TotalMinutes} min, "
                       + $"{result.PromptTokens + result.CompletionTokens:N0} tokens; ended: {result.EndReason}.\n");
        message.Append($"Team branch: {result.TeamBranch}\n");
        var summaries = result.Board.Where(e => e.Type == TeamBoardEntryType.PatchSummary).Take(50).ToList();
        if (summaries.Count > 0)
        {
            message.Append("\nWhat the workers reported:\n");
            foreach (var entry in summaries)
            {
                message.Append($"- {entry.Author}: {entry.Text}\n");
            }
        }

        return message.ToString().TrimEnd();
    }

    private static string? UnsavedAmong(IReadOnlyList<string> changed, string repositoryRoot, IReadOnlySet<string> unsaved)
    {
        var open = changed.Where(p => unsaved.Contains(Path.GetFullPath(Path.Combine(repositoryRoot, p)))).ToList();
        return open.Count == 0
            ? null
            : $"you have unsaved edits open in {string.Join(", ", open.Take(3))}{(open.Count > 3 ? $" and {open.Count - 3} more" : "")}";
    }

    private static bool SameText(string a, string b) =>
        string.Equals(a.Replace("\r\n", "\n"), b.Replace("\r\n", "\n"), StringComparison.Ordinal);

    private async Task DeleteRunBranchesAsync(string repositoryRoot, IReadOnlyList<string> branches)
    {
        var failed = new List<string>();
        foreach (var branch in branches)
        {
            if (!(await _teamGit.DeleteBranchAsync(repositoryRoot, branch)).Success)
            {
                failed.Add(branch);
            }
        }

        StatusMessage = failed.Count == 0
            ? $"Deleted {branches.Count} branch{(branches.Count == 1 ? "" : "es")} from the Ultra run."
            : $"Could not delete: {string.Join(", ", failed)}.";
        await RefreshUltraStartOptionsAsync();
    }

    /// <summary>What one Ultra run's workers have been allowed to run: exact command lines, or -
    /// once the user chooses "Allow all for this run" - anything, until the run ends.</summary>
    private sealed class TeamShellApprovals
    {
        public HashSet<string> Commands { get; } = new(StringComparer.Ordinal);

        public bool AllowAll { get; set; }

        /// <summary>Raised on the UI thread when AllowAll is granted, so the run's card can say so.</summary>
        public Action? AllowAllGranted { get; set; }
    }

    /// <summary>
    /// run_command for Ultra workers: the approval dialog, marshalled onto the UI thread from worker
    /// threads and serialized, with the run's own approvals.
    ///
    /// Not the session's: the same command line is a different command in a worktree, whose files -
    /// including any script the command runs - the team writes without review. An approval from the
    /// user's own checkout must not carry into a run, nor one from a run back out of it. Within a run
    /// one approval covers every worker, as the user chose, and the prompt says so - including that it
    /// holds after workers change files the command reads. "Allow all for this run" goes further, at
    /// the user's word: no more prompts from this team until the run ends.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunTeamShellCommandAsync(
        string command, string reason, string workingDirectory, TeamShellApprovals approvals, CancellationToken cancellationToken)
    {
        var promptReason = $"{reason}\n\nRuns in an Ultra worker's own worktree ({workingDirectory}), whose files the "
                           + "team may have changed. Allow lets every worker in this run use this exact command "
                           + "until the run ends - even after they change files it reads.";
        string? refusal;
        await _teamShellApprovals.WaitAsync(cancellationToken);
        try
        {
            refusal = await _dispatcher.InvokeAsync(
                () => ApproveTeamShellCommandAsync(command, promptReason, approvals)).Task.Unwrap();
        }
        finally
        {
            _teamShellApprovals.Release();
        }

        return refusal is not null
            ? (true, refusal)
            : await RunApprovedShellCommandAsync(command, workingDirectory, cancellationToken);
    }

    /// <summary>UI thread only, like ApproveShellCommandAsync, which it falls back to when the host
    /// offers no three-way prompt.</summary>
    private async Task<string?> ApproveTeamShellCommandAsync(string command, string reason, TeamShellApprovals approvals)
    {
        if (!AreShellCommandsEnabled)
        {
            return "Shell commands are turned off. The user can enable them in the Copilot Chat pane.";
        }

        if (approvals.AllowAll || approvals.Commands.Contains(command))
        {
            return null;
        }

        if (TeamShellCommandApprovalPrompt is not { } prompt)
        {
            return await ApproveShellCommandAsync(command, reason, approvals.Commands);
        }

        switch (await prompt(command, reason))
        {
            case ApprovalChoice.AllowAll:
                approvals.AllowAll = true;
                approvals.AllowAllGranted?.Invoke();
                return null;

            case ApprovalChoice.Allow:
                approvals.Commands.Add(command);
                return null;

            default:
                return "The user declined to run that command. Do not try to run it again, or work around "
                       + "the refusal with a different command that does the same thing - post a FAIL saying "
                       + "what you needed it for instead.";
        }
    }
}
