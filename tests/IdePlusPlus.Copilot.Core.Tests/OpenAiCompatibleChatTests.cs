using System.Text.Json;
using IdePlusPlus.Copilot.Core;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class OpenAiCompatibleChatTests
{
    [Fact]
    public void ToRequestMessage_PlainMessage_SerializesAsRoleAndContent()
    {
        var json = Serialize(OpenAiCompatibleChat.ToRequestMessage(new ChatMessage(ChatRole.User, "hi")));

        json.GetProperty("role").GetString().ShouldBe("user");
        json.GetProperty("content").GetString().ShouldBe("hi");
        json.TryGetProperty("tool_calls", out _).ShouldBeFalse();
    }

    [Fact]
    public void ToRequestMessage_AssistantWithToolCalls_SerializesToolCallsArray()
    {
        var message = new ChatMessage(
            ChatRole.Assistant,
            "",
            ToolCalls: [new ChatToolCall("call-1", "playwright__navigate", """{"url":"https://example.com"}""")]);

        var json = Serialize(OpenAiCompatibleChat.ToRequestMessage(message));

        json.GetProperty("role").GetString().ShouldBe("assistant");
        json.GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Null);
        var toolCall = json.GetProperty("tool_calls")[0];
        toolCall.GetProperty("id").GetString().ShouldBe("call-1");
        toolCall.GetProperty("type").GetString().ShouldBe("function");
        toolCall.GetProperty("function").GetProperty("name").GetString().ShouldBe("playwright__navigate");
        toolCall.GetProperty("function").GetProperty("arguments").GetString().ShouldBe("""{"url":"https://example.com"}""");
    }

    [Fact]
    public void ToRequestMessage_ToolResult_SerializesRoleToolWithToolCallId()
    {
        var message = new ChatMessage(ChatRole.Tool, "the result text", ToolCallId: "call-1");

        var json = Serialize(OpenAiCompatibleChat.ToRequestMessage(message));

        json.GetProperty("role").GetString().ShouldBe("tool");
        json.GetProperty("tool_call_id").GetString().ShouldBe("call-1");
        json.GetProperty("content").GetString().ShouldBe("the result text");
    }

    [Fact]
    public void ToRequestMessage_AssistantWithNonEmptyContentAndToolCalls_KeepsContent()
    {
        var message = new ChatMessage(
            ChatRole.Assistant,
            "Let me check that for you.",
            ToolCalls: [new ChatToolCall("call-1", "some_tool", "{}")]);

        var json = Serialize(OpenAiCompatibleChat.ToRequestMessage(message));

        json.GetProperty("content").GetString().ShouldBe("Let me check that for you.");
    }

    private static JsonElement Serialize(object message) =>
        JsonDocument.Parse(JsonSerializer.Serialize(message)).RootElement;
}
