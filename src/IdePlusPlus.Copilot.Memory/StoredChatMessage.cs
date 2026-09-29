namespace IdePlusPlus.Copilot.Memory;

/// <summary>Mirrors Copilot.Core's ChatToolCall so a tool call can round-trip through storage.</summary>
public sealed record StoredToolCall(string Id, string FunctionName, string ArgumentsJson);

/// <summary>Role is the plain string name of a Copilot.Core ChatRole ("User"/"Assistant"/"Tool") - kept
/// as a string here rather than referencing that enum, since IdePlusPlus.Copilot.Memory otherwise has
/// zero project dependencies and this preserves that.
///
/// ToolCallId/ToolCalls mirror ChatMessage's own optional fields, so a tool call and its result can
/// be persisted too, not just plain prose. Both default to null and are optional constructor
/// parameters, so a chat-history.json written before these existed - which only ever has "Role" and
/// "Text" - still deserializes cleanly.</summary>
public sealed record StoredChatMessage(
    string Role,
    string Text,
    string? ToolCallId = null,
    IReadOnlyList<StoredToolCall>? ToolCalls = null);
