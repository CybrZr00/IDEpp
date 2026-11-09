namespace IdePlusPlus.Shell;

/// <summary>What the user chose when New Chat warned that it would discard something.</summary>
public enum NewConversationChoice
{
    /// <summary>Keep the conversation as it is.</summary>
    Cancel,

    /// <summary>Start the new conversation, withdrawing whatever was undecided.</summary>
    Discard,

    /// <summary>Accept every undecided edit first, then start the new conversation.</summary>
    AcceptAllThenStart,
}
