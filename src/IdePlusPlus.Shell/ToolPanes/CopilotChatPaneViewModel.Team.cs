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
    public const int DefaultUltraWorkerCount = 4;
    public const int MaxUltraWorkerCount = 16;
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

    /// <summary>Drives the pane's Ultra banner, which says what choosing it means before anything runs.</summary>
    public bool IsUltraEffort => SelectedEffort == ChatEffort.Ultra;

    public static int ReadUltraWorkerCount(AppSettingsStore settings) =>
        int.TryParse(settings.Get(UltraWorkerCountSettingKey), out var count)
            ? Math.Clamp(count, 1, MaxUltraWorkerCount)
            : DefaultUltraWorkerCount;

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
    private async Task RunUltraTeamAsync(
        string goal, List<ChatMessage> fallbackHistory, ChatMessageViewModel assistantMessage, string? fallbackContextQuery)
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

                var options = new UltraTeamOptions(
                    ReadUltraWorkerCount(_appSettingsStore),
                    TimeSpan.FromMinutes(ReadUltraTimeBudgetMinutes(_appSettingsStore)),
                    AreShellCommandsEnabled,
                    _currentConfiguration?.Invoke() ?? "Debug",
                    ChatEffort.Ultra);
                IChatClient client = SelectedProvider == ProviderLmStudio ? _lmStudioClient : _openRouterClient;
                var run = new UltraTeamRun(
                    client, SelectedModel, options, _readOnlyToolDispatcher, _gitService, _teamGit,
                    TimeProvider.System, UltraRootDirectory, RunTeamShellCommandAsync);

                var teamBranch = TeamGitWorkspace.GetTeamBranchName(run.RunId);
                card = new TeamRunViewModel(
                    run, SelectedProvider == ProviderLmStudio, () => DiscardTeamBranchAsync(repositoryRoot, teamBranch));
                Messages.Insert(Messages.IndexOf(assistantMessage), card);
                CurrentActivity = $"Ultra team working - {options.WorkerCount} workers, up to {options.TimeBudget.TotalMinutes:0} min...";

                var solutionPath = _solutionPath;
                var testProjects = _testProjectPaths;
                // On the thread pool: the run's own continuations then stay off the UI thread rather
                // than queueing behind it for every tool call of every worker.
                var result = await Task.Run(
                    () => run.RunAsync(goal, repositoryRoot, solutionPath, testProjects, turn.Token), turn.Token);
                ThrowIfDetached(turn);

                card.Finish(result);
                TotalPromptTokens += result.PromptTokens;
                TotalCompletionTokens += result.CompletionTokens;
                CurrentActivity = "Preparing the team's changes for review...";
                await ProposeTeamResultAsync(run, result, assistantMessage, turn.Token);
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
    private async Task ProposeTeamResultAsync(
        UltraTeamRun run, UltraTeamResult result, ChatMessageViewModel assistantMessage, CancellationToken cancellationToken)
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

        assistantMessage.Text = DescribeTeamResult(run, result, proposals.Count, notProposed);

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

    private static string DescribeTeamResult(
        UltraTeamRun run, UltraTeamResult result, int proposalCount, IReadOnlyList<string> notProposed)
    {
        var text = new StringBuilder("**Ultra team** - ");
        text.Append(result.EndReason switch
        {
            UltraTeamEndReason.AllDone => "finished: every worker reported done.",
            UltraTeamEndReason.TimeUp => "stopped: the time budget ran out.",
            UltraTeamEndReason.Cancelled => "stopped.",
            _ => "stopped: no worker was still making progress.",
        });
        text.Append($" {result.Workers.Count} workers, {(int)run.Elapsed.TotalMinutes} min, "
                    + $"{result.PromptTokens + result.CompletionTokens:N0} tokens.\n\n");

        if (result.Changes.Count == 0)
        {
            text.Append("Nothing was merged, so there is nothing to review.\n");
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
            text.Append("\nWhat the workers merged:\n");
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

        text.Append("\nWorkers:\n");
        foreach (var worker in result.Workers)
        {
            text.Append(worker.IsDone
                ? $"- {worker.Name}: done - {worker.DoneSummary}\n"
                : $"- {worker.Name}: stopped after {worker.Steps} steps\n");
        }

        return text.ToString().TrimEnd();
    }

    private static bool SameText(string a, string b) =>
        string.Equals(a.Replace("\r\n", "\n"), b.Replace("\r\n", "\n"), StringComparison.Ordinal);

    private async Task DiscardTeamBranchAsync(string repositoryRoot, string teamBranch)
    {
        var result = await _teamGit.DeleteBranchAsync(repositoryRoot, teamBranch);
        StatusMessage = result.Success ? $"Deleted {teamBranch}." : $"Could not delete {teamBranch}: {result.Output}";
    }

    /// <summary>
    /// run_command for Ultra workers. The approval is the same one the single agent uses - same
    /// dialog, same per-command memory, so approving a command once covers every worker - but it is
    /// reached from worker threads, so it is marshalled onto the UI thread and serialized.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunTeamShellCommandAsync(
        string command, string reason, string workingDirectory, CancellationToken cancellationToken)
    {
        string? refusal;
        await _teamShellApprovals.WaitAsync(cancellationToken);
        try
        {
            refusal = await _dispatcher.InvokeAsync(() => ApproveShellCommandAsync(command, reason)).Task.Unwrap();
        }
        finally
        {
            _teamShellApprovals.Release();
        }

        return refusal is not null
            ? (true, refusal)
            : await RunApprovedShellCommandAsync(command, workingDirectory, cancellationToken);
    }
}
