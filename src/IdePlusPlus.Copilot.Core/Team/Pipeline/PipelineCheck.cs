using System.Text;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// What a validator made of something a model submitted: the cleaned-up value when it is acceptable,
/// or every reason it is not. The reasons are written to be handed straight back to the model as the
/// tool's result - specific, one per line, saying what to change - because a model told "invalid" tries
/// the same thing again, and one told "owner b3 is not in the owners list" fixes it.
/// </summary>
public sealed record PipelineCheck<T>(T? Value, IReadOnlyList<string> Errors)
{
    /// <summary>The most reasons one refusal lists; the rest are counted. A model shown forty problems
    /// fixes the first few and resubmits, so the long tail only costs context.</summary>
    public const int MaxListedErrors = 12;

    public bool IsValid => Errors.Count == 0;

    /// <summary>The reasons as a bulleted list for a tool result.</summary>
    public string Feedback
    {
        get
        {
            var listed = Errors.Take(MaxListedErrors).Select(e => "- " + e);
            var more = Errors.Count > MaxListedErrors ? $"\n- ... and {Errors.Count - MaxListedErrors} more." : "";
            return string.Join("\n", listed) + more;
        }
    }

    public static PipelineCheck<T> Ok(T value) => new(value, []);

    public static PipelineCheck<T> Failed(IEnumerable<string> errors) => new(default, [.. errors]);
}

/// <summary>Small text helpers the pipeline's validators share.</summary>
internal static partial class PipelineText
{
    public const string TruncationMark = "...[truncated]";

    /// <summary>Trimmed, with runs of whitespace (and any control characters) collapsed to one space;
    /// null and blank become the empty string.</summary>
    public static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : Whitespace().Replace(text.Trim(), " ");

    /// <summary>The same normalisation used to compare a quoted line with a file: whitespace is
    /// insignificant, case and everything else are not.</summary>
    public static string ForComparison(string? text) => Clean(text);

    /// <summary><paramref name="text"/> cut to <paramref name="max"/> characters, ending with a mark
    /// that says so.</summary>
    public static string Truncate(string text, int max, out bool truncated)
    {
        truncated = text.Length > max;
        return truncated ? text[..Math.Max(0, max - TruncationMark.Length)] + TruncationMark : text;
    }

    public static string Quote(string? text) => $"'{Clean(text)}'";

    /// <summary>A short label for item <paramref name="index"/> of a list, as 1-based text for a message.</summary>
    public static string Item(string list, int index) => $"{list}[{index + 1}]";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Collects the reasons something was refused.</summary>
internal sealed class ErrorList
{
    private readonly List<string> _errors = [];

    public void Add(string error) => _errors.Add(error);

    public bool Any => _errors.Count > 0;

    public IReadOnlyList<string> ToList() => _errors;

    public PipelineCheck<T> ResultOr<T>(Func<T> value) => Any ? PipelineCheck<T>.Failed(_errors) : PipelineCheck<T>.Ok(value());

    public override string ToString() => new StringBuilder().AppendJoin("; ", _errors).ToString();
}
