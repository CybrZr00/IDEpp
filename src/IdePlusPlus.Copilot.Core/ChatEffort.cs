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
}
