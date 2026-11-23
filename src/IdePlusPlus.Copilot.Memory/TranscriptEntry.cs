using System.Text.Json.Serialization;

namespace IdePlusPlus.Copilot.Memory;

/// <summary>What a <see cref="TranscriptEntry"/> is. Plain strings, not an enum, for the same reason
/// StoredChatMessage keeps Role a string: this project has no dependencies, and a new kind added
/// later must not make an older transcript file unreadable.</summary>
public static class TranscriptKinds
{
    /// <summary>Something the user said (or an instruction the IDE sent on their behalf).</summary>
    public const string User = "user";

    /// <summary>What the assistant said back.</summary>
    public const string Assistant = "assistant";

    /// <summary>The model's reasoning trace for a reply, where the provider streamed one. Stored apart
    /// from the reply itself so a search can ask for either.</summary>
    public const string Thought = "thought";
}

/// <summary>
/// One line of a conversation's transcript: a single user message, assistant message or thought.
///
/// Sequence counts up from 1 within a conversation, so (ConversationId, Sequence) names an entry for
/// good - <see cref="Id"/> - which is what lets an embedding saved on disk be matched back to it
/// after a restart. Source says where it came from when that is not obvious ("plan approved").
/// </summary>
public sealed record TranscriptEntry(
    string ConversationId,
    int Sequence,
    DateTimeOffset Timestamp,
    string Kind,
    string Text,
    string? Source = null)
{
    [JsonIgnore]
    public string Id => $"{ConversationId}#{Sequence}";
}
