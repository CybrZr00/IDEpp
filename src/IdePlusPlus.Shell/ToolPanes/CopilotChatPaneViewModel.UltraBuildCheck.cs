using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// The build check before an Ultra message. Every merge a team makes needs a passing build, so a
/// solution that does not build cannot take any of its work - and in Plan Mode the single agent has
/// already read half the codebase and written a plan by the time the run finds that out. Building
/// first, in the user's own tree, costs one incremental build and spends no model tokens.
///
/// In its own file for the same reason as the team run: it shares little with the turn loop beyond the
/// turn scope and the build helper.
/// </summary>
public sealed partial class CopilotChatPaneViewModel
{
    /// <summary>A solution that built a moment ago is not built again for the next Ultra message - a
    /// revised plan, say. A run still builds its own copy before any worker starts, so a change made
    /// in between is caught there.</summary>
    private static readonly TimeSpan UltraBuildCheckFreshFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Asked when the solution does not build as an Ultra message is sent, with the build's report.
    /// Unset, nothing is checked: the question has nowhere to go, and the run's own preflight build
    /// still stops a run on a solution that cannot build.
    /// </summary>
    public Func<string, UltraBuildChoice>? UltraBuildFailedPrompt { get; set; }

    private DateTimeOffset? _ultraBuildVerifiedAt;

    /// <summary>True for the turn that fixes the build: Plan Mode would otherwise take propose_edit away
    /// from it, and fixing is exactly what it has been asked to do.</summary>
    private bool _editsAllowedInPlanMode;

    private bool PlanToolsActive => IsPlanMode && !_editsAllowedInPlanMode;

    /// <summary>
    /// Builds the open solution before an Ultra message is acted on. Returns true to go on (it builds,
    /// it could not be checked, or the user chose to go ahead anyway) and false when nothing more
    /// should happen to this message - the user cancelled, or a turn to fix the build has run instead.
    /// Either way the message is put back in the input box, so nothing typed is lost.
    /// </summary>
    private async Task<bool> EnsureBuildsBeforeUltraAsync(string userText)
    {
        if (UltraBuildFailedPrompt is not { } ask || _solutionPath is null)
        {
            return true;
        }

        if (_ultraBuildVerifiedAt is { } verifiedAt && DateTimeOffset.UtcNow - verifiedAt < UltraBuildCheckFreshFor)
        {
            return true;
        }

        BuildOutcome outcome;
        var turn = BeginTurn("Checking that the solution builds before the Ultra run...");
        try
        {
            outcome = await BuildCoreAsync(project: null, turn.Token);
            ThrowIfDetached(turn);
        }
        catch (OperationCanceledException) when (turn.Token.IsCancellationRequested || !IsLive(turn))
        {
            if (IsLive(turn))
            {
                RestoreInput(userText);
                StatusMessage = "Stopped before the Ultra run started.";
            }

            return false;
        }
        finally
        {
            EndTurn(turn);
        }

        if (outcome.Succeeded)
        {
            _ultraBuildVerifiedAt = DateTimeOffset.UtcNow;
            return true;
        }

        if (outcome.NotRun)
        {
            // Not the same as a failed build: a build already running, or one too slow to wait for, says
            // nothing about whether the solution builds. The run's own preflight is still ahead.
            StatusMessage = $"Couldn't check the build before the Ultra run - {outcome.Report}";
            return true;
        }

        switch (ask(outcome.Report))
        {
            case UltraBuildChoice.Continue:
                return true;

            case UltraBuildChoice.FixFirst:
                await FixBuildBeforeUltraAsync(userText, outcome.Report);
                return false;

            default:
                RestoreInput(userText);
                StatusMessage = "Ultra not started - the solution doesn't build. Fix the build and send again.";
                return false;
        }
    }

    private void RestoreInput(string userText)
    {
        if (string.IsNullOrEmpty(InputText))
        {
            InputText = userText;
        }
    }

    /// <summary>
    /// A normal single-agent turn whose only job is a building solution. Its edits are proposals like
    /// any other, so the user accepts them (or Auto Mode does) before anything changes; the original
    /// message goes back in the input box to send once they have, and the build is checked again then.
    /// </summary>
    private async Task FixBuildBeforeUltraAsync(string userText, string report)
    {
        var marker = new ChatMessageViewModel(ChatRole.User) { Text = "Fix the build before the Ultra run" };
        Messages.Add(marker);

        var instruction = $"""
            The solution does not build, and an Ultra team run needs one that does: every change the team
            makes is merged only after a passing build. Fix the build errors first, so the run can start
            from a working solution. Change only what stops the build - no refactoring and nothing
            unrelated. Use build_solution to check your work, and propose_edit for each change. When the
            build succeeds, say so in one sentence and stop.

            The build report:
            {report}
            """;
        _lastUserRequestText = instruction;

        var history = BuildChatHistory(Messages.Where(m => m != marker));
        history.Add(new ChatMessage(ChatRole.User, instruction));

        var assistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
        Messages.Add(assistantMessage);

        _editsAllowedInPlanMode = true;
        try
        {
            await RunAssistantTurnsAsync(history, assistantMessage, contextQuery: "fix the build errors");
        }
        finally
        {
            _editsAllowedInPlanMode = false;
        }

        RestoreInput(userText);
        StatusMessage ??= "Accept the fixes, then send your message again - the build is checked again before the Ultra run starts.";
    }
}
