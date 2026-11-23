using System.Runtime.CompilerServices;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Shell.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The two chat-client wrappers every Ultra run puts between its agents and the provider. They are
/// exercised through whole team runs in UltraTeamRunTests; this pins them on their own, since the
/// pipeline run uses them without a team run around them.
/// </summary>
public sealed class TeamChatClientsTests
{
    private static readonly IReadOnlyList<ChatMessage> Messages = [new ChatMessage(ChatRole.User, "hello")];

    private static UltraTeamOptions Options(int retries = 2, int maxConcurrent = 16) =>
        new(1, TimeSpan.FromMinutes(1), false, "Debug", ChatEffort.Medium)
        {
            RateLimitRetries = retries,
            RateLimitBackoff = TimeSpan.Zero,
            MaxConcurrentModelRequests = maxConcurrent,
        };

    private static async Task<List<ChatStreamEvent>> CollectAsync(IChatClient client)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in client.StreamChatAsync(Messages, "model"))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    // --- Usage counting ---

    [Fact]
    public async Task UsageCounting_ReportsEachUsageEvent_AndPassesEveryEventThroughInOrder()
    {
        var inner = new FakeClient(_ =>
        [
            new TextDelta("a"),
            new UsageReceived(100, 10, 40),
            new TextDelta("b"),
            new UsageReceived(5, 1),
        ]);
        var reported = new List<UsageReceived>();
        var client = new UsageCountingChatClient(inner, reported.Add);

        var events = await CollectAsync(client);

        events.ShouldBe(
        [
            new TextDelta("a"),
            new UsageReceived(100, 10, 40),
            new TextDelta("b"),
            new UsageReceived(5, 1),
        ]);
        reported.ShouldBe([new UsageReceived(100, 10, 40), new UsageReceived(5, 1)]);
    }

    [Fact]
    public async Task UsageCounting_WithNoUsageInTheReply_NeverCallsBack()
    {
        var calls = 0;
        var client = new UsageCountingChatClient(new FakeClient(_ => [new TextDelta("hi")]), _ => calls++);

        (await CollectAsync(client)).ShouldBe([new TextDelta("hi")]);

        calls.ShouldBe(0);
    }

    // --- Throttling ---

    [Fact]
    public async Task Throttling_RetriesARateLimitedFirstEvent_ThenPassesTheRealReplyOn()
    {
        var inner = new FakeClient(call => call < 3
            ? [new StreamError("OpenRouter returned 429: rate limited")]
            : [new TextDelta("ok")]);
        var client = new ThrottlingChatClient(inner, Options(retries: 2), TimeProvider.System);

        var events = await CollectAsync(client);

        events.ShouldBe([new TextDelta("ok")]);
        inner.Calls.ShouldBe(3);
    }

    [Theory]
    [InlineData("HTTP 503 service unavailable")]
    [InlineData("529 overloaded")]
    [InlineData("You are being Rate-Limited")]
    [InlineData("the model is overloaded right now")]
    public async Task Throttling_RecognisesTheUsualRateLimitWordings(string message)
    {
        var inner = new FakeClient(call => call == 1 ? [new StreamError(message)] : [new TextDelta("ok")]);
        var client = new ThrottlingChatClient(inner, Options(), TimeProvider.System);

        (await CollectAsync(client)).ShouldBe([new TextDelta("ok")]);
        inner.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Throttling_GivesUpAfterTheConfiguredRetries_AndHandsTheErrorOn()
    {
        var inner = new FakeClient(_ => [new StreamError("429 rate limited")]);
        var client = new ThrottlingChatClient(inner, Options(retries: 2), TimeProvider.System);

        var events = await CollectAsync(client);

        events.ShouldBe([new StreamError("429 rate limited")]);
        inner.Calls.ShouldBe(3); // The first attempt and two retries.
    }

    [Fact]
    public async Task Throttling_DoesNotRetryAnErrorThatIsNotARateLimit()
    {
        var inner = new FakeClient(_ => [new StreamError("invalid api key")]);
        var client = new ThrottlingChatClient(inner, Options(), TimeProvider.System);

        (await CollectAsync(client)).ShouldBe([new StreamError("invalid api key")]);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Throttling_DoesNotRetryOnceAnythingHasReachedTheCaller()
    {
        // A retry is only safe when the refusal is the stream's first event: after text has been
        // delivered, replaying the request would show the worker the same reply twice.
        var inner = new FakeClient(_ => [new TextDelta("partial"), new StreamError("429 rate limited")]);
        var client = new ThrottlingChatClient(inner, Options(), TimeProvider.System);

        (await CollectAsync(client)).ShouldBe([new TextDelta("partial"), new StreamError("429 rate limited")]);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Throttling_AnEmptyReply_EndsWithoutAnError()
    {
        var inner = new FakeClient(_ => []);
        var client = new ThrottlingChatClient(inner, Options(), TimeProvider.System);

        (await CollectAsync(client)).ShouldBeEmpty();
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Throttling_NeverHasMoreRequestsInFlightThanTheConfiguredCap()
    {
        var inner = new FakeClient(_ => [new TextDelta("ok")], delay: TimeSpan.FromMilliseconds(25));
        var client = new ThrottlingChatClient(inner, Options(maxConcurrent: 2), TimeProvider.System);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CollectAsync(client)));

        inner.Calls.ShouldBe(8);
        inner.MaxInFlight.ShouldBe(2);
    }

    [Fact]
    public async Task Throttling_GivesTheSlotBackWhenTheCallerStopsEarly()
    {
        // A worker that is cancelled mid-stream must not keep holding a slot for the rest of the run.
        var inner = new FakeClient(_ => [new TextDelta("1"), new TextDelta("2"), new TextDelta("3")]);
        var client = new ThrottlingChatClient(inner, Options(maxConcurrent: 1), TimeProvider.System);

        await foreach (var _ in client.StreamChatAsync(Messages, "model"))
        {
            break; // Disposing the enumerator is what releases the slot.
        }

        var second = await CollectAsync(client).WaitAsync(TimeSpan.FromSeconds(10));

        second.Count.ShouldBe(3);
    }

    private sealed class FakeClient(Func<int, IReadOnlyList<ChatStreamEvent>> reply, TimeSpan delay = default) : IChatClient
    {
        private int _calls;
        private int _inFlight;
        private int _maxInFlight;

        public int Calls => Volatile.Read(ref _calls);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition>? tools = null,
            ChatEffort effort = ChatEffort.None,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            var inFlight = Interlocked.Increment(ref _inFlight);
            int max;
            while (inFlight > (max = Volatile.Read(ref _maxInFlight))
                   && Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max)
            {
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                foreach (var streamEvent in reply(call))
                {
                    yield return streamEvent;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
