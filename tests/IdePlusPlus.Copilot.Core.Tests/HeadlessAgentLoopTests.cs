using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class HeadlessAgentLoopTests
{
    private static ToolCallDelta ToolCall(int index, string id, string name, string argumentsJson) =>
        new(index, id, name, argumentsJson);

    [Fact]
    public async Task RunAsync_SendsTheSystemPromptAndTaskAsTheInitialRequest()
    {
        var client = new FakeChatClient([new TextDelta("ok")]);
        var loop = new HeadlessAgentLoop(client, "model-x", ChatEffort.None, [], (_, _, _) => Task.FromResult((false, "")));

        await loop.RunAsync("you are a reviewer", "investigate the widget", maxIterations: 5, CancellationToken.None);

        client.Requests.ShouldHaveSingleItem();
        client.Requests[0].Count.ShouldBe(2);
        client.Requests[0][0].Role.ShouldBe(ChatRole.System);
        client.Requests[0][0].Content.ShouldBe("you are a reviewer");
        client.Requests[0][1].Role.ShouldBe(ChatRole.User);
        client.Requests[0][1].Content.ShouldBe("investigate the widget");
    }

    [Fact]
    public async Task RunAsync_WithNoToolCalls_ReturnsTheTextAfterOneIteration()
    {
        var client = new FakeChatClient([new TextDelta("The answer "), new TextDelta("is 42.")]);
        var loop = new HeadlessAgentLoop(client, "model-x", ChatEffort.None, [], (_, _, _) => Task.FromResult((false, "")));

        var result = await loop.RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        result.FinalText.ShouldBe("The answer is 42.");
        result.Iterations.ShouldBe(1);
        result.ToolCallCount.ShouldBe(0);
        result.HitIterationCap.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WithAToolCall_ExecutesItAndFeedsTheResultIntoTheNextTurn()
    {
        var client = new FakeChatClient(
            [ToolCall(0, "call-1", "read_file", """{"file":"Foo.cs"}""")],
            [new TextDelta("Foo.cs looks fine.")]);

        var executedCalls = new List<(string Name, string? File)>();
        Func<string, JsonNode?, CancellationToken, Task<(bool, string)>> executeTool = (name, arguments, _) =>
        {
            executedCalls.Add((name, arguments?["file"]?.GetValue<string>()));
            return Task.FromResult((false, "class Foo {}"));
        };

        var loop = new HeadlessAgentLoop(client, "model-x", ChatEffort.None, [], executeTool);

        var result = await loop.RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        executedCalls.ShouldHaveSingleItem();
        executedCalls[0].Name.ShouldBe("read_file");
        executedCalls[0].File.ShouldBe("Foo.cs");

        result.FinalText.ShouldBe("Foo.cs looks fine.");
        result.Iterations.ShouldBe(2);
        result.ToolCallCount.ShouldBe(1);

        // The second request must carry the assistant's tool call and its result, or the model
        // would be asked to continue a conversation it can't see the middle of.
        client.Requests.Count.ShouldBe(2);
        var secondRequest = client.Requests[1];
        secondRequest.ShouldContain(m => m.Role == ChatRole.Assistant && m.ToolCalls != null && m.ToolCalls[0].Id == "call-1");
        secondRequest.ShouldContain(m => m.Role == ChatRole.Tool && m.ToolCallId == "call-1" && m.Content == "class Foo {}");
    }

    [Fact]
    public async Task RunAsync_WhenTheModelKeepsCallingTools_StopsAtMaxIterationsAndSaysSo()
    {
        var client = new FakeChatClient(
            [ToolCall(0, "call-1", "read_file", "{}")],
            [ToolCall(0, "call-2", "read_file", "{}")],
            [ToolCall(0, "call-3", "read_file", "{}")]);

        var loop = new HeadlessAgentLoop(
            client, "model-x", ChatEffort.None, [], (_, _, _) => Task.FromResult((false, "content")));

        var result = await loop.RunAsync("system", "task", maxIterations: 2, CancellationToken.None);

        result.HitIterationCap.ShouldBeTrue();
        result.Iterations.ShouldBe(2);
        result.ToolCallCount.ShouldBe(2);
        client.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_AccumulatesUsageAcrossEveryIteration()
    {
        var client = new FakeChatClient(
            [ToolCall(0, "call-1", "read_file", "{}"), new UsageReceived(PromptTokens: 100, CompletionTokens: 20)],
            [new TextDelta("done"), new UsageReceived(PromptTokens: 150, CompletionTokens: 10)]);

        var loop = new HeadlessAgentLoop(
            client, "model-x", ChatEffort.None, [], (_, _, _) => Task.FromResult((false, "content")));

        var result = await loop.RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        result.PromptTokens.ShouldBe(250);
        result.CompletionTokens.ShouldBe(30);
    }

    /// <summary>A tool call with arguments the model streamed as invalid JSON must not crash the
    /// loop or silently vanish - it becomes a tool result explaining the problem, same as a real
    /// malformed-arguments case elsewhere in the app, and the conversation continues.</summary>
    [Fact]
    public async Task RunAsync_WithMalformedToolArguments_ReportsItWithoutCallingTheDelegate()
    {
        var client = new FakeChatClient(
            [ToolCall(0, "call-1", "read_file", "{not valid json")],
            [new TextDelta("recovered")]);

        var delegateWasCalled = false;
        Func<string, JsonNode?, CancellationToken, Task<(bool, string)>> executeTool = (_, _, _) =>
        {
            delegateWasCalled = true;
            return Task.FromResult((false, "should not happen"));
        };

        var loop = new HeadlessAgentLoop(client, "model-x", ChatEffort.None, [], executeTool);
        var result = await loop.RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        delegateWasCalled.ShouldBeFalse();
        result.FinalText.ShouldBe("recovered");

        var secondRequest = client.Requests[1];
        secondRequest.ShouldContain(m => m.Role == ChatRole.Tool && m.Content.Contains("Malformed arguments"));
    }

    [Fact]
    public async Task RunAsync_WithEmptyArguments_TreatsThemAsNoArguments()
    {
        var client = new FakeChatClient([ToolCall(0, "call-1", "list_files", "")], []);

        JsonNode? receivedArguments = new JsonObject(); // sentinel, overwritten if the delegate runs
        var loop = new HeadlessAgentLoop(client, "model-x", ChatEffort.None, [], (_, arguments, _) =>
        {
            receivedArguments = arguments;
            return Task.FromResult((false, "ok"));
        });

        await loop.RunAsync("system", "task", maxIterations: 5, CancellationToken.None);

        receivedArguments.ShouldBeNull();
    }
}
