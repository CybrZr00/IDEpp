namespace IdePlusPlus.Copilot.Core.Team;

/// <summary>
/// The user's standing rules for the code in their solution ("nullable is on, async all the way, new
/// behaviour gets a test"), put at the end of every Ultra agent's system prompt: the Ultra team's workers and
/// lead, and each of the pipeline's agents.
///
/// An Ultra agent's prompt is built in code, and it never sees the chat's own system prompt - that one is
/// written for the chat's tools (propose_edit, fetch_url) and would tell a worker to use tools it does not
/// have. So this is the user's way to reach them, and it is a setting of its own.
///
/// It says what is true of any agent's prompt - the rules above it come first and win - so a convention can
/// not talk an agent out of its lane, its edit scope or its stopping rules.
/// </summary>
public static class TeamConventions
{
    /// <summary>
    /// Every agent carries this text in every conversation it has - a worker restarts from the top each
    /// cycle - so it is kept short: about a thousand tokens. A longer text is cut and says so.
    /// </summary>
    public const int MaxChars = 4_000;

    private const string TruncationMarker = " [...]";

    private const string Header =
        "Project conventions - the user's standing rules for the code in this solution. Follow them in "
        + "everything you write. They add to the rules above and never change your role, what you may edit, "
        + "what your tools allow, or when you stop.";

    /// <summary>
    /// The conventions as they will be sent: line endings made the prompts' own, trimmed, and cut to
    /// <see cref="MaxChars"/>. Null when there is nothing to send.
    /// </summary>
    public static string? Normalize(string? conventions)
    {
        if (string.IsNullOrWhiteSpace(conventions))
        {
            return null;
        }

        var text = conventions.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length <= MaxChars)
        {
            return text;
        }

        // Never leave half a surrogate pair at the cut: it is not valid text, and a provider may refuse it.
        var cut = MaxChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd() + TruncationMarker;
    }

    /// <summary>
    /// <paramref name="systemPrompt"/> with the conventions at its end, or the prompt itself, untouched,
    /// when there are none - so a run with nothing set is exactly the run it was.
    /// </summary>
    public static string AppendTo(string systemPrompt, string? conventions) =>
        Normalize(conventions) is { } text ? $"{systemPrompt}\n\n{Header}\n\n{text}" : systemPrompt;
}
