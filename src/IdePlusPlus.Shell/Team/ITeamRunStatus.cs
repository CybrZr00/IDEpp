using IdePlusPlus.Copilot.Core.Team;

namespace IdePlusPlus.Shell.Team;

/// <summary>
/// What a status card and a result summary read from an Ultra run, whichever kind it is: the team of
/// workers (<see cref="UltraTeamRun"/>) or the staged pipeline (<c>UltraPipelineRun</c>). All of
/// it is thread-safe to read while the run is going on - the card polls it from the UI thread.
///
/// A member that means nothing to one kind has a plain answer rather than being left out, so the card
/// needs no case for which it is: the pipeline has no rounds the user is asked to continue, no auto
/// mode, and no board a worker writes to.
/// </summary>
public interface ITeamRunStatus
{
    string RunId { get; }

    /// <summary>What the agents posted - for the pipeline, nothing: its agents hand in artifacts, and the
    /// runner keeps the record.</summary>
    TeamBoard Board { get; }

    /// <summary>The agents so far, in the order they joined. A copy of the moment it was read: the team
    /// grows in auto mode, and the pipeline adds each stage's agents as the stage starts.</summary>
    IReadOnlyList<TeamWorkerState> Workers { get; }

    int MaxWorkers { get; }

    bool IsAutoScaling { get; }

    TimeSpan TimeBudget { get; }

    TimeSpan Elapsed { get; }

    TimeSpan TimeLeft { get; }

    /// <summary>1 until the user continues a run whose time ran out; always 1 for the pipeline.</summary>
    int Round { get; }

    /// <summary>What the run is doing before its first agent starts, or null once it is under way.</summary>
    string? SetupStatus { get; }

    bool IsCheckingBuild { get; }

    bool IsAwaitingContinue { get; }

    string? PauseExplanation { get; }

    bool IsWrappingUp { get; }

    /// <summary>Which stage the run is in, in a few words - "Judge, loop 2 of 3" - or null for a run that
    /// has no stages (the team) or has not reached its first.</summary>
    string? StageText { get; }

    /// <summary>True for the staged pipeline: its card and its words say "pipeline" and "agents", not "team" and "workers".</summary>
    bool IsPipeline { get; }

    /// <summary>Where the run keeps its records (the pipeline's phases folder, with the report in it), or null for a run that keeps none.</summary>
    string? RecordsDirectory { get; }

    /// <summary>The token budget the run ends at, or 0 for none.</summary>
    long MaxTokens { get; }

    int PromptTokens { get; }

    int CompletionTokens { get; }

    /// <summary>The part of <see cref="PromptTokens"/> providers served from their prompt cache.</summary>
    int CachedPromptTokens { get; }

    /// <summary>The branch this run carries on from, or null for a run from the working tree alone.</summary>
    string? StartedFrom { get; }

    /// <summary>How many board entries the run took over from the earlier run it carries on from.</summary>
    int CarriedBoardEntries { get; }

    /// <summary>Something a status display would show has changed. Raised on whatever thread caused it,
    /// often - a subscriber that touches UI should marshal and throttle.</summary>
    event Action? Changed;
}

/// <summary>An Ultra run the chat pane can start: it reports its status and runs to a result.</summary>
public interface IUltraRun : ITeamRunStatus
{
    /// <param name="startFrom">A branch to carry on from, merged into the working-tree snapshot so the
    /// agents start with both.</param>
    /// <param name="backlog">The steps the user approved in Plan Mode, if there were any.</param>
    Task<UltraTeamResult> RunAsync(
        string goal,
        string repositoryRoot,
        string solutionPath,
        IReadOnlyList<string> testProjectPaths,
        CancellationToken cancellationToken,
        string? startFrom = null,
        IReadOnlyList<string>? backlog = null);
}
