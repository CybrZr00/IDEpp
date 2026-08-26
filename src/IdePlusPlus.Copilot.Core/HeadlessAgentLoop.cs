using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>Richer than a bare string so a caller can report "may be incomplete" or account for
/// spend, rather than silently dropping that information.</summary>
public sealed record HeadlessRunResult(
    string FinalText, int Iterations, int ToolCallCount, bool HitIterationCap, int PromptTokens, int CompletionTokens);

/// <summary>
/// A small, WPF-free agent loop for headless callers - sub-agents (investigate_parallel) today,
/// scheduled automations later. Deliberately not a reuse of
/// CopilotChatPaneViewModel.StreamOneTurnAsync/RunAssistantTurnsAsync: those are the most exercised
/// path in the app (edit/plan-proposal parsing, dispatcher-marshaled UI updates, Auto Mode
/// bookkeeping), and headless callers never offer propose_edit/propose_plan - folding this in would
/// add real risk to the primary conversation for no matching benefit. This handles only
/// TextDelta/ToolCallDelta; there is nothing WPF-shaped anywhere in it, so it can run on any thread.
///
/// One instance is one independent conversation - its own history, starting from just a system
/// prompt and a task. Tool execution is provided by the caller as a delegate rather than this class
/// knowing how to run any particular tool, so the same loop works for a sub-agent (dispatching into
/// the primary ViewModel's existing read-only handlers) and, later, a scheduled run (dispatching
/// into whatever handlers that context provides) without either caller needing to know about the
/// other.
/// </summary>
public sealed class HeadlessAgentLoop
{
    private readonly IChatClient _client;
    private readonly string _model;
    private readonly ChatEffort _effort;
    private readonly IReadOnlyList<ToolDefinition> _tools;
    private readonly Func<string, JsonNode?, CancellationToken, Task<(bool IsError, string Text)>> _executeTool;

    public HeadlessAgentLoop(
        IChatClient client,
        string model,
        ChatEffort effort,
        IReadOnlyList<ToolDefinition> tools,
        Func<string, JsonNode?, CancellationToken, Task<(bool IsError, string Text)>> executeTool)
    {
        _client = client;
        _model = model;
        _effort = effort;
        _tools = tools;
        _executeTool = executeTool;
    }

    public async Task<HeadlessRunResult> RunAsync(
        string systemPrompt, string task, int maxIterations, CancellationToken cancellationToken)
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, task),
        };

        var finalText = "";
        var toolCallCount = 0;
        var promptTokens = 0;
        var completionTokens = 0;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var turn = await StreamOneTurnAsync(history, cancellationToken).ConfigureAwait(false);
            finalText = turn.Text;
            promptTokens += turn.PromptTokens;
            completionTokens += turn.CompletionTokens;

            if (turn.Calls.Count == 0)
            {
                return new HeadlessRunResult(finalText, iteration + 1, toolCallCount, HitIterationCap: false, promptTokens, completionTokens);
            }

            history.Add(new ChatMessage(
                ChatRole.Assistant,
                turn.Text,
                ToolCalls: turn.Calls.Select(c => new ChatToolCall(c.Id, c.Name, c.ArgumentsJson)).ToList()));

            foreach (var call in turn.Calls)
            {
                toolCallCount++;

                JsonNode? arguments;
                try
                {
                    arguments = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? null : JsonNode.Parse(call.ArgumentsJson);
                }
                catch (JsonException)
                {
                    history.Add(new ChatMessage(
                        ChatRole.Tool, $"Malformed arguments JSON for '{call.Name}'.", ToolCallId: call.Id));
                    continue;
                }

                var (isError, resultText) = await _executeTool(call.Name, arguments, cancellationToken).ConfigureAwait(false);
                history.Add(new ChatMessage(ChatRole.Tool, resultText, ToolCallId: call.Id));
            }
        }

        return new HeadlessRunResult(finalText, maxIterations, toolCallCount, HitIterationCap: true, promptTokens, completionTokens);
    }

    private async Task<TurnResult> StreamOneTurnAsync(List<ChatMessage> history, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var callsByIndex = new Dictionary<int, PendingCall>();
        var promptTokens = 0;
        var completionTokens = 0;

        await foreach (var streamEvent in _client.StreamChatAsync(history, _model, _tools, _effort, cancellationToken)
            .ConfigureAwait(false))
        {
            switch (streamEvent)
            {
                case TextDelta delta:
                    text.Append(delta.Text);
                    break;

                case ToolCallDelta toolCallDelta:
                    if (!callsByIndex.TryGetValue(toolCallDelta.Index, out var call))
                    {
                        call = new PendingCall(toolCallDelta.Index);
                        callsByIndex[toolCallDelta.Index] = call;
                    }

                    if (toolCallDelta.Id is not null)
                    {
                        call.Id = toolCallDelta.Id;
                    }

                    if (toolCallDelta.FunctionName is not null)
                    {
                        call.Name = toolCallDelta.FunctionName;
                    }

                    if (toolCallDelta.ArgumentsFragment is not null)
                    {
                        call.Arguments.Append(toolCallDelta.ArgumentsFragment);
                    }

                    break;

                case StreamError error:
                    text.Append($"\n\n[error: {error.Message}]");
                    break;

                case FinishReasonReceived { Reason: "length" }:
                    text.Append("\n\n[response was cut off - it hit the model's output length limit before finishing]");
                    break;

                case UsageReceived usage:
                    promptTokens += usage.PromptTokens;
                    completionTokens += usage.CompletionTokens;
                    break;
            }
        }

        var calls = callsByIndex.Values
            .Where(c => c.Name is not null)
            .Select(c => new ResolvedCall(c.EffectiveId, c.Name!, c.Arguments.ToString()))
            .ToList();

        return new TurnResult(text.ToString(), calls, promptTokens, completionTokens);
    }

    private sealed record TurnResult(string Text, List<ResolvedCall> Calls, int PromptTokens, int CompletionTokens);

    private sealed record ResolvedCall(string Id, string Name, string ArgumentsJson);

    private sealed class PendingCall(int index)
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Arguments = new();

        public string EffectiveId => Id ?? $"call-{index}";
    }
}
