using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>The onModelTurn callback: how a headless agent's thoughts and replies get out of it.</summary>
public class HeadlessAgentLoopModelTurnTests
{
    private static HeadlessAgentLoop Loop(
        IChatClient client,
        List<(string? Reasoning, string Text)> turns,
        Func<string, System.Text.Json.Nodes.JsonNode?, CancellationToken, Task<(bool IsError, string Text)>>? executeTool = null) =>
        new(
            client, "model-x", ChatEffort.None, [],
            executeTool ?? ((_, _, _) => Task.FromResult((false, "tool result"))),
            onModelTurn: (reasoning, text) => turns.Add((reasoning, text)));

    [Fact]
    public async Task EachModelRequest_ReportsItsReasoningAndText()
    {
        var client = new FakeChatClient(
            [new ReasoningDelta("First "), new ReasoningDelta("thought"), new TextDelta("Looking."), new ToolCallDelta(0, "c1", "look", "{}")],
            [new TextDelta("All done.")]);
        var turns = new List<(string? Reasoning, string Text)>();

        await Loop(client, turns).RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        turns.ShouldBe([("First thought", "Looking."), (null, "All done.")]);
    }

    [Fact]
    public async Task ATurnWithOnlyAToolCall_ReportsNothing()
    {
        var client = new FakeChatClient(
            [new ToolCallDelta(0, "c1", "look", "{}")],
            [new TextDelta("Done.")]);
        var turns = new List<(string? Reasoning, string Text)>();

        await Loop(client, turns).RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        turns.ShouldBe([(null, "Done.")]);
    }

    [Fact]
    public async Task TheNotesTheLoopAddsToAReply_AreNotReportedAsWhatTheModelSaid()
    {
        var client = new FakeChatClient(
            [new TextDelta("Half an answer"), new FinishReasonReceived("length"), new StreamError("connection reset")]);
        var turns = new List<(string? Reasoning, string Text)>();

        var result = await Loop(client, turns).RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        result.FinalText.ShouldContain("[error: connection reset]");
        turns.ShouldBe([(null, "Half an answer")]);
    }

    [Fact]
    public async Task ARequestCancelledPartWay_StillReportsWhatHadArrived()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new CancellingClient(cancellation);
        var turns = new List<(string? Reasoning, string Text)>();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Loop(client, turns).RunAsync("system", "task", maxIterations: 5, cancellation.Token));

        turns.ShouldBe([("Half a thought", "Half an answer")]);
    }

    [Fact]
    public async Task AListenerThatThrows_CannotStopTheAgent()
    {
        var client = new FakeChatClient([new TextDelta("Fine.")]);
        var loop = new HeadlessAgentLoop(
            client, "model-x", ChatEffort.None, [], (_, _, _) => Task.FromResult((false, "")),
            onModelTurn: (_, _) => throw new InvalidOperationException("listener broke"));

        var result = await loop.RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        result.FinalText.ShouldBe("Fine.");
    }

    [Fact]
    public async Task WithNoListener_NothingChanges()
    {
        var client = new FakeChatClient([new ReasoningDelta("t"), new TextDelta("Fine.")]);
        var loop = new HeadlessAgentLoop(client, "model-x", ChatEffort.None, [], (_, _, _) => Task.FromResult((false, "")));

        (await loop.RunAsync("system", "task", maxIterations: 5, CancellationToken.None)).FinalText.ShouldBe("Fine.");
    }

    /// <summary>Streams a partial reply and then the caller's cancellation arrives.</summary>
    private sealed class CancellingClient(CancellationTokenSource cancellation) : IChatClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition>? tools = null,
            ChatEffort effort = ChatEffort.None,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ReasoningDelta("Half a thought");
            yield return new TextDelta("Half an answer");
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextDelta("never seen");
        }
    }
}
