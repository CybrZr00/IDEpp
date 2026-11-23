using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Shell.Team;

/// <summary>
/// The team's share of the provider: at most <see cref="UltraTeamOptions.MaxConcurrentModelRequests"/>
/// requests in flight, and a request refused for rate limiting retried with backoff instead of
/// handed to the worker as a failed step. Shared by every kind of Ultra run.
///
/// A retry is only safe because the refusal is the stream's first and only event - nothing has
/// reached the worker yet. The slot is given back while backing off, so one rate-limited worker
/// does not hold up the others that might get through.
/// </summary>
internal sealed class ThrottlingChatClient(IChatClient inner, UltraTeamOptions options, TimeProvider time) : IChatClient
{
    private static readonly Regex RateLimited = new(@"\b(429|503|529)\b|rate.?limit|overloaded", RegexOptions.IgnoreCase);

    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.MaxConcurrentModelRequests));

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools = null,
        ChatEffort effort = ChatEffort.None,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _slots.WaitAsync(cancellationToken);
            var released = false;
            try
            {
                await using var events = inner.StreamChatAsync(messages, model, tools, effort, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                if (!await events.MoveNextAsync())
                {
                    yield break;
                }

                if (events.Current is StreamError error && RateLimited.IsMatch(error.Message) && attempt < options.RateLimitRetries)
                {
                    _slots.Release();
                    released = true;
                    await Task.Delay(Backoff(attempt), time, cancellationToken);
                    continue;
                }

                yield return events.Current;
                while (await events.MoveNextAsync())
                {
                    yield return events.Current;
                }

                yield break;
            }
            finally
            {
                if (!released)
                {
                    _slots.Release();
                }
            }
        }
    }

    /// <summary>Doubling from the configured base, capped at a minute, with jitter so workers
    /// refused together do not all come back together.</summary>
    private TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(
            Math.Min(60_000, options.RateLimitBackoff.TotalMilliseconds * Math.Pow(2, attempt))
            * (0.75 + Random.Shared.NextDouble() * 0.5));
}

/// <summary>Reports each response's usage to <paramref name="onUsage"/> as it arrives, so a run's
/// token count moves during a cycle rather than only when one finishes. A callback rather than the
/// run itself, so the team run and the pipeline run can each keep their own totals.</summary>
internal sealed class UsageCountingChatClient(IChatClient inner, Action<UsageReceived> onUsage) : IChatClient
{
    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools = null,
        ChatEffort effort = ChatEffort.None,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in inner.StreamChatAsync(messages, model, tools, effort, cancellationToken))
        {
            if (streamEvent is UsageReceived usage)
            {
                onUsage(usage);
            }

            yield return streamEvent;
        }
    }
}
