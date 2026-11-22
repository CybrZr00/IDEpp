namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>Why a stage has used up its share. The wording is what an agent is told and what the
/// record keeps, so it reads as a sentence.</summary>
public enum StageOverReason
{
    Time,
    Tokens,
}

/// <summary>
/// A run's whole allowance of time and tokens, and the thing that hands each stage its share of what is
/// left (see <see cref="StageBudget"/> for the arithmetic). One per run; the run starts it once, when
/// the stages begin, and asks it for a <see cref="StageClock"/> as each agent stage starts.
///
/// Time is measured on the run's <see cref="TimeProvider"/>, so a test that drives time sees a stage
/// run out. Tokens are read through a callback, because the run's own client wrapper is what counts
/// them as responses arrive.
/// </summary>
public sealed class PipelineBudget
{
    private readonly TimeProvider _time;
    private readonly Func<long> _tokensUsed;
    private DateTimeOffset _startedAt;
    private bool _started;

    /// <param name="timeBudget">The run's time. Zero or negative means the run has none left.</param>
    /// <param name="maxTokens">The run's tokens, prompt plus completion; zero or less means unlimited.</param>
    /// <param name="maxRepairLoops">How many repair rounds the run may do - it shapes the schedule the
    /// shares are cut from.</param>
    /// <param name="hardStopGrace">How long past its share a stage's agents may take to finish the step
    /// they are on before they are cancelled.</param>
    /// <param name="tokensUsed">The run's tokens so far.</param>
    public PipelineBudget(
        TimeProvider time,
        TimeSpan timeBudget,
        long maxTokens,
        int maxRepairLoops,
        TimeSpan hardStopGrace,
        Func<long> tokensUsed)
    {
        _time = time;
        TimeBudget = timeBudget;
        MaxTokens = Math.Max(0, maxTokens);
        HardStopGrace = hardStopGrace < TimeSpan.Zero ? TimeSpan.Zero : hardStopGrace;
        _tokensUsed = tokensUsed;
        Schedule = StageBudget.Schedule(maxRepairLoops);
    }

    public TimeSpan TimeBudget { get; }

    /// <summary>Zero when the run has no token limit.</summary>
    public long MaxTokens { get; }

    public TimeSpan HardStopGrace { get; }

    public IReadOnlyList<StageSlot> Schedule { get; }

    public TimeProvider Time => _time;

    /// <summary>Starts the clock. Everything before this (the snapshot, the preflight build) is the
    /// run's setup and is not charged to a stage; calling it again changes nothing.</summary>
    public void Start()
    {
        if (!_started)
        {
            _startedAt = _time.GetUtcNow();
            _started = true;
        }
    }

    public TimeSpan Elapsed => _started ? _time.GetUtcNow() - _startedAt : TimeSpan.Zero;

    public TimeSpan TimeLeft
    {
        get
        {
            var left = TimeBudget - Elapsed;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public long TokensUsed => _tokensUsed();

    /// <summary>The tokens left, or null when the run has no token limit.</summary>
    public long? TokensLeft => MaxTokens <= 0 ? null : Math.Max(0, MaxTokens - TokensUsed);

    /// <summary>The run has no time or no tokens left, so no further stage should start.</summary>
    public bool IsSpent => TimeLeft <= TimeSpan.Zero || TokensLeft is 0;

    /// <summary>A sentence for the end of the run, or null while there is something left.</summary>
    public string? SpentBecause =>
        TimeLeft <= TimeSpan.Zero
            ? $"The time budget is spent ({TimeBudget.TotalMinutes:0.#} min)."
            : TokensLeft is 0
                ? $"The token budget is spent: {TokensUsed:N0} of {MaxTokens:N0} tokens used."
                : null;

    /// <summary>
    /// The clock for one stage, starting now. A stage on the schedule gets its weight's share of what is
    /// left, in both time and tokens; one that is not on it - a judge round beyond the cap, or a stage
    /// with no weight - gets all that is left, so the whole run's budget is still the limit.
    /// </summary>
    /// <param name="round">Which round of the stage (the judge and repair rounds count from 1; the
    /// others use 0).</param>
    public StageClock BeginStage(UltraStage stage, int round = 0)
    {
        Start();
        var index = IndexOf(stage, round);
        var timeLeft = TimeLeft;
        var timeShare = index >= 0 && StageBudget.WeightOf(stage) > 0
            ? StageBudget.Share(timeLeft, Schedule, index)
            : timeLeft;

        long? tokenShare = null;
        if (TokensLeft is { } tokensLeft)
        {
            tokenShare = index >= 0 && StageBudget.WeightOf(stage) > 0
                ? StageBudget.Share(tokensLeft, Schedule, index)
                : tokensLeft;
        }

        return new StageClock(this, stage, round, timeShare, tokenShare);
    }

    private int IndexOf(UltraStage stage, int round)
    {
        for (var i = 0; i < Schedule.Count; i++)
        {
            if (Schedule[i].Stage == stage && Schedule[i].Round == round)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// One stage's share of the run, as the agents of that stage see it. It is over when the stage's time
/// or token share is used up (or the run's own is): an agent that sees that is asked to submit what it
/// has. <see cref="HardStop"/> is the backstop - it is cancelled <see cref="PipelineBudget.HardStopGrace"/>
/// after the share ends, for an agent that is not stopping between steps.
///
/// Disposing it releases the hard-stop timer; do that when the stage is done.
/// </summary>
public sealed class StageClock : IDisposable
{
    private readonly PipelineBudget _budget;
    private readonly DateTimeOffset _startedAt;
    private readonly long _tokensAtStart;
    private readonly CancellationTokenSource _hardStop;

    internal StageClock(PipelineBudget budget, UltraStage stage, int round, TimeSpan timeShare, long? tokenShare)
    {
        _budget = budget;
        Stage = stage;
        Round = round;
        TimeShare = timeShare < TimeSpan.Zero ? TimeSpan.Zero : timeShare;
        TokenShare = tokenShare;
        _startedAt = budget.Time.GetUtcNow();
        _tokensAtStart = budget.TokensUsed;

        // The share plus the grace. The share was cut from what the run had left, so this is never later
        // than the run's own deadline plus the grace.
        var stop = TimeShare + budget.HardStopGrace;
        _hardStop = stop <= TimeSpan.Zero
            ? new CancellationTokenSource()
            : new CancellationTokenSource(stop, budget.Time);
        if (stop <= TimeSpan.Zero)
        {
            _hardStop.Cancel();
        }
    }

    public UltraStage Stage { get; }

    public int Round { get; }

    public TimeSpan TimeShare { get; }

    /// <summary>The tokens this stage may spend, or null when the run has no token limit.</summary>
    public long? TokenShare { get; }

    public TimeSpan Elapsed => _budget.Time.GetUtcNow() - _startedAt;

    /// <summary>The stage's own time left, never negative.</summary>
    public TimeSpan TimeLeft
    {
        get
        {
            var left = TimeShare - Elapsed;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>The tokens the run has spent since this stage began.</summary>
    public long TokensUsed => Math.Max(0, _budget.TokensUsed - _tokensAtStart);

    public long? TokensLeft => TokenShare is { } share ? Math.Max(0, share - TokensUsed) : null;

    /// <summary>Cancelled at the hard limit. An agent run takes this as part of its own token, so a step
    /// that will not end is cut off rather than holding the stage up.</summary>
    public CancellationToken HardStop => _hardStop.Token;

    /// <summary>The share is used up. (The run's own allowance cannot run out first: a share is cut from
    /// what the run has left when the stage starts, and a stage that is not on the schedule is given all
    /// of it.)</summary>
    public bool IsOver => OverBecause is not null;

    public StageOverReason? OverBecause =>
        TimeLeft <= TimeSpan.Zero ? StageOverReason.Time
        : TokensLeft is 0 ? StageOverReason.Tokens
        : null;

    /// <summary>"Judge 2", "Build" - the stage and round, for a status line.</summary>
    public override string ToString() =>
        Round > 0 ? $"{Stage} {Round}" : Stage.ToString();

    public void Dispose() => _hardStop.Dispose();
}
