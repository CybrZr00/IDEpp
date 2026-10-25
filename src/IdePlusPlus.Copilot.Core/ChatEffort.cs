namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Maps to OpenRouter's normalized `reasoning: { effort }` request field (silently ignored by
/// models that don't support extended reasoning, per OpenRouter's own docs). None omits the field
/// entirely rather than sending a value, so a provider/model with no opinion on it sees the exact
/// same request shape as before this feature existed.
/// </summary>
public enum ChatEffort
{
    None,
    Low,
    Medium,
    High,

    /// <summary>
    /// High reasoning on every request, and the chat pane runs a send as an Ultra team - several
    /// workers in their own git worktrees, coordinating through a shared board (see
    /// IdePlusPlus.Copilot.Core.Team). Last so a persisted "ChatEffort" setting written before it
    /// existed still parses to the same value. Anywhere that only sends one request - sub-agents,
    /// Review Last Change - treats it as High.
    /// </summary>
    Ultra,
}
