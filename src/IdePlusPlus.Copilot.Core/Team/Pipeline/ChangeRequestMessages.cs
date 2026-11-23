namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// What each party is told about a change request. Everything another agent wrote - the change asked for, the
/// reasons, an owner's refusal, the lead's instruction - goes in a data fence, like every other piece of one agent's
/// words that reaches another's prompt; the ids, names and states around it are the runner's own.
///
/// Pure text: the run decides when to say these, and this is what it says.
/// </summary>
public static class ChangeRequestMessages
{
    /// <summary>The request as its owner reads it.</summary>
    public static string Incoming(ChangeRequest request) =>
        DataFence.Wrap(
            $"request {request.Id}",
            $"{request.From} asks {request.To} to change {request.File}.\nChange: {request.Change}\nWhy: {request.Rationale}");

    /// <summary>Appended to an owner's next tool result when a request for it arrives while it is working.</summary>
    public static string IncomingNotice(ChangeRequest request) =>
        $"{request.Id} is waiting for your answer.\n{Incoming(request)}\n"
        + "Answer it before anything else - you cannot edit, commit or merge until you do. respond_change accepts it (then make the "
        + $"change in your own file, build, team_commit, team_merge, and call change_done {request.Id}) or rejects it with reasons "
        + "the requester can act on.";

    /// <summary>Said to an owner the lead has ruled must make a change it refused.</summary>
    public static string BindingNotice(ChangeRequest request) =>
        $"The lead has ruled that you must make the change {request.Id} asks for - the decision is final.\n"
        + DataFence.Wrap($"ruling on {request.Id}", $"{Incoming(request)}\nThe lead's instruction: {request.Arbitration}")
        + $"\nMake it in your own file, build, team_commit, team_merge, then call change_done {request.Id}.";

    /// <summary>The refusal an owner gets for editing, committing or merging while a request waits for its answer.</summary>
    public static string GuardRefusal(string tool, IReadOnlyList<ChangeRequest> pending)
    {
        var first = pending[0];
        var also = pending.Count > 1 ? $" ({pending.Count - 1} more after it: {string.Join(", ", pending.Skip(1).Select(r => r.Id))})" : "";
        return $"Refused: {tool}. {first.Id} from {first.From} is waiting for your answer{also} - answer it first.\n{Incoming(first)}\n"
               + "respond_change accepts it or rejects it with reasons; then carry on.";
    }

    /// <summary>
    /// Why an owner cannot report that it is done while requests involve it, or null when none does. Unanswered requests
    /// to it would be left unanswered; requests it made and has not seen through would be left with nobody to read the answer.
    /// </summary>
    public static string? BlocksReport(IReadOnlyList<ChangeRequest> open, string owner)
    {
        var toYou = open.Where(r => string.Equals(r.To, owner, StringComparison.OrdinalIgnoreCase)).ToList();
        if (toYou.FirstOrDefault(r => r.State == ChangeRequestState.Pending) is { } pending)
        {
            return $"{pending.Id} from {pending.From} is waiting for your answer: respond_change first.";
        }

        if (toYou.FirstOrDefault(r => r.State == ChangeRequestState.Accepted) is { } accepted)
        {
            return $"You accepted {accepted.Id} and have not reported it done: make the change, merge it, then change_done {accepted.Id}.";
        }

        var yours = open.Where(r => string.Equals(r.From, owner, StringComparison.OrdinalIgnoreCase)).ToList();
        return yours.FirstOrDefault() is { } mine
            ? $"Your request {mine.Id} to {mine.To} is still open: await_change {mine.Id} until it is settled, then report."
            : null;
    }

    /// <summary>What await_change tells a waiter, as (is it an error, text).</summary>
    public static (bool IsError, string Text) AwaitResult(ChangeWaitResult wait)
    {
        if (!wait.Ok)
        {
            return (true, wait.Error!);
        }

        var request = wait.Request!;
        if (wait.WaitTimedOut)
        {
            return (false, $"{request.Id} is still {Describe(request.State)} after the wait. await_change again, or carry on with your own files and come back.");
        }

        return (false, Outcome(request));
    }

    /// <summary>Where a request stands, for the one who made it.</summary>
    public static string Outcome(ChangeRequest request) => request.State switch
    {
        ChangeRequestState.Pending =>
            $"{request.Id} has no answer yet from {request.To}. await_change again, or carry on and come back.",

        ChangeRequestState.Accepted =>
            $"{request.To} accepted {request.Id}"
            + (request.Binding ? " - the lead ruled that it must" : "")
            + $".{Reason(request)} It will make the change and report it done: await_change {request.Id} again to wait for that.",

        ChangeRequestState.Rejected when request.AppealUsed =>
            $"{request.To} rejected {request.Id}, and the lead upheld it.{Reason(request)}{Ruling(request)} The decision is final: carry on without it.",

        ChangeRequestState.Rejected =>
            $"{request.To} rejected {request.Id}.{Reason(request)} You may appeal once with appeal_change (say why those reasons do not hold) "
            + "- the lead decides - or carry on without the change.",

        ChangeRequestState.Appealed => $"{request.Id} is with the lead for a decision. await_change {request.Id} again.",

        ChangeRequestState.Done =>
            $"{request.To} made the change for {request.Id}" + (request.DoneSummary is { Length: > 0 } ? $": {Quote(request.DoneSummary)}" : "")
            + ". team_sync to bring it into your worktree, then carry on.",

        ChangeRequestState.TimedOut => $"{request.Id} timed out.{Reason(request)} Carry on without it, or ask again.",

        _ => $"{request.Id} was cancelled.{Reason(request)} Carry on without it, or ask again.",
    };

    /// <summary>What appeal_change tells the requester once the lead has ruled.</summary>
    public static string AppealRuling(ChangeRequest request) => request.State == ChangeRequestState.Accepted
        ? $"The lead ruled for you: {request.To} must make the change for {request.Id}.{Ruling(request)} await_change {request.Id} for it to be done."
        : $"The lead ruled for {request.To}: the refusal of {request.Id} stands.{Ruling(request)} The decision is final: carry on without the change.";

    private static string Reason(ChangeRequest request) =>
        request.Reasoning is { Length: > 0 } reasoning ? $" {Quote(reasoning)}" : "";

    private static string Ruling(ChangeRequest request) =>
        request.Arbitration is { Length: > 0 } ruling ? $" The lead said: {Quote(ruling)}" : "";

    /// <summary>Other agents' words are inline here, so they are quoted and any fence marker in them is taken out.</summary>
    private static string Quote(string text) => $"\"{DataFence.Strip(text)}\"";

    private static string Describe(ChangeRequestState state) => state switch
    {
        ChangeRequestState.Pending => "waiting for the owner",
        ChangeRequestState.Accepted => "accepted but not done",
        ChangeRequestState.Appealed => "with the lead",
        _ => state.ToString().ToLowerInvariant(),
    };
}
