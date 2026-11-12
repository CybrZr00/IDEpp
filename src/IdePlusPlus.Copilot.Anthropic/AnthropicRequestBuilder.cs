using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Copilot.Anthropic;

/// <summary>
/// Turns the provider-neutral conversation into a Messages API request body. The differences from
/// the OpenAI shape that matter: system prompts are a top-level field rather than messages; tool
/// results are <c>tool_result</c> blocks inside a user message (so a run of them, and any user text
/// that follows, become one user message); and tool-call arguments are sent as a parsed object.
/// </summary>
public static class AnthropicRequestBuilder
{
    /// <summary>Output-token cap when the model's own limit is unknown, or above this. Generous
    /// enough for a long file edit in one tool call; the cap is a ceiling, not a target.</summary>
    public const int DefaultMaxTokens = 32000;

    /// <param name="replayLookup">The assistant content blocks Claude originally returned for the
    /// response whose first tool call has the given id, if still known - replayed verbatim so the
    /// thinking blocks (with their signatures) go back exactly as received, which the API requires
    /// within a tool-use turn.</param>
    public static JsonObject Build(
        IReadOnlyList<ChatMessage> messages,
        AnthropicModelInfo model,
        IReadOnlyList<ToolDefinition>? tools,
        ChatEffort effort,
        Func<string, JsonArray?>? replayLookup = null)
    {
        replayLookup ??= _ => null;

        var maxTokens = Math.Min(model.MaxOutputTokens ?? DefaultMaxTokens, DefaultMaxTokens);
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
        };

        var system = messages
            .Where(m => m.Role == ChatRole.System && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = m.Content })
            .ToArray();
        if (system.Length > 0)
        {
            body["system"] = new JsonArray(system);
        }

        var thinking = ThinkingFor(effort, model, maxTokens);

        // Manual thinking needs the current turn's first assistant message to start with its original
        // thinking block. When that can't be replayed (the app restarted mid-turn, or the turn began on
        // another provider), thinking is left off for this request rather than failing it.
        if (thinking?["type"]?.GetValue<string>() == "enabled" && !CanReplayCurrentTurn(messages, replayLookup))
        {
            thinking = null;
        }

        body["messages"] = BuildMessages(messages, replayLookup);

        if (tools is { Count: > 0 })
        {
            body["tools"] = new JsonArray(tools.Select(ToToolSchema).ToArray());
            body["tool_choice"] = new JsonObject { ["type"] = "auto" };
        }

        if (thinking is not null)
        {
            body["thinking"] = thinking;
            if (thinking["budget_tokens"] is { } budget)
            {
                body["max_tokens"] = Math.Max(maxTokens, budget.GetValue<int>() + 4096);
            }
        }

        if (EffortLevel(effort) is { } level && model.SupportsEffortLevel(level))
        {
            body["output_config"] = new JsonObject { ["effort"] = level };
        }

        // Automatic prompt caching: one marker the API places on the last cacheable block. An agent
        // loop resends the whole conversation every step, so each step after the first reads the
        // earlier part back from the cache at a fraction of the price.
        body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        return body;
    }

    /// <summary>Ultra is High on the wire - what makes it Ultra is how many workers send requests,
    /// not any one request. Null at None, so no opinion is sent the user never expressed.</summary>
    public static string? EffortLevel(ChatEffort effort) => effort switch
    {
        ChatEffort.Low => "low",
        ChatEffort.Medium => "medium",
        ChatEffort.High or ChatEffort.Ultra => "high",
        _ => null,
    };

    /// <summary>
    /// At None the field is omitted: the current models then think adaptively on their own (with the
    /// text hidden), and older ones don't think. Otherwise adaptive thinking with summarised text, so
    /// the reasoning shows in the chat, or a token budget on a model that only has manual thinking.
    /// </summary>
    private static JsonObject? ThinkingFor(ChatEffort effort, AnthropicModelInfo model, int maxTokens)
    {
        if (effort == ChatEffort.None)
        {
            return null;
        }

        if (model.SupportsAdaptiveThinking)
        {
            return new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
        }

        if (!model.SupportsManualThinking)
        {
            return null;
        }

        var budget = effort switch
        {
            ChatEffort.Low => 2048,
            ChatEffort.Medium => 8192,
            _ => 16000,
        };

        // budget_tokens must be below max_tokens; on a model whose output limit is small, the budget
        // gives way rather than the answer.
        if (model.MaxOutputTokens is { } limit && budget + 4096 > limit)
        {
            budget = limit - 4096;
        }

        return budget >= 1024 ? new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget } : null;
    }

    private static bool CanReplayCurrentTurn(IReadOnlyList<ChatMessage> messages, Func<string, JsonArray?> replayLookup)
    {
        var lastUserText = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                lastUserText = i;
                break;
            }
        }

        var firstAssistant = messages.Skip(lastUserText + 1).FirstOrDefault(m => m.Role == ChatRole.Assistant);
        if (firstAssistant is null)
        {
            return true;
        }

        return firstAssistant.ToolCalls is { Count: > 0 } calls
               && replayLookup(calls[0].Id) is { } replay
               && replay.FirstOrDefault()?["type"]?.GetValue<string>() is "thinking" or "redacted_thinking";
    }

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages, Func<string, JsonArray?> replayLookup)
    {
        var result = new JsonArray();
        string? lastRole = null;
        JsonArray? lastContent = null;

        void Append(string role, IEnumerable<JsonNode> blocks)
        {
            var list = blocks.ToList();
            if (list.Count == 0)
            {
                return;
            }

            // Consecutive same-role messages are merged: tool results followed by the loop's own
            // follow-up text must be one user message, and the API wants roles to alternate anyway.
            if (role == lastRole && lastContent is not null)
            {
                foreach (var block in list)
                {
                    lastContent.Add(block);
                }

                return;
            }

            lastContent = new JsonArray(list.ToArray());
            lastRole = role;
            result.Add(new JsonObject { ["role"] = role, ["content"] = lastContent });
        }

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.System:
                    break;

                case ChatRole.User:
                    Append("user", [TextBlock(string.IsNullOrWhiteSpace(message.Content) ? "(empty message)" : message.Content)]);
                    break;

                case ChatRole.Tool:
                    Append("user",
                    [
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = SanitizeId(message.ToolCallId),
                            ["content"] = string.IsNullOrEmpty(message.Content) ? "(no output)" : message.Content,
                        },
                    ]);
                    break;

                case ChatRole.Assistant:
                    Append("assistant", AssistantBlocks(message, replayLookup));
                    break;
            }
        }

        return result;
    }

    private static IEnumerable<JsonNode> AssistantBlocks(ChatMessage message, Func<string, JsonArray?> replayLookup)
    {
        if (message.ToolCalls is not { Count: > 0 } toolCalls)
        {
            return string.IsNullOrWhiteSpace(message.Content) ? [] : [TextBlock(message.Content.TrimEnd())];
        }

        // Replayed only when it still describes this exact message - same tool calls, same order.
        if (replayLookup(toolCalls[0].Id) is { } replay)
        {
            var replayIds = replay
                .Where(b => b?["type"]?.GetValue<string>() == "tool_use")
                .Select(b => b!["id"]?.GetValue<string>())
                .ToList();
            if (replayIds.SequenceEqual(toolCalls.Select(c => (string?)c.Id)))
            {
                return replay.Select(b => b!.DeepClone()).ToList();
            }
        }

        var blocks = new List<JsonNode>();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            blocks.Add(TextBlock(message.Content.TrimEnd()));
        }

        foreach (var call in toolCalls)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = SanitizeId(call.Id),
                ["name"] = call.FunctionName,
                ["input"] = ParseArguments(call.ArgumentsJson),
            });
        }

        return blocks;
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ToToolSchema(ToolDefinition tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["input_schema"] = tool.ParametersSchema.DeepClone(),
    };

    private static JsonObject ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Tool ids must match <c>^[a-zA-Z0-9_-]+$</c>. Ids from another provider (a conversation
    /// that switched to Claude part-way) can contain other characters; tool_use and tool_result are
    /// sanitised the same way, so they still pair up.</summary>
    public static string SanitizeId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "toolu_missing";
        }

        var builder = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.ToString();
    }
}
