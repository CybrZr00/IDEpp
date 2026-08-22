namespace IdePlusPlus.Copilot.Memory;

/// <summary>Role is the plain string name of a Copilot.Core ChatRole ("User"/"Assistant") - kept as
/// a string here rather than referencing that enum, since IdePlusPlus.Copilot.Memory otherwise has
/// zero project dependencies and this preserves that.</summary>
public sealed record StoredChatMessage(string Role, string Text);
