using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IdePlusPlus.Copilot.Anthropic;

/// <summary>
/// What a Claude model accepts, as far as the request shape is concerned. Claude models differ in
/// ways that turn into 400s rather than being ignored: models before Opus 4.6 only take manual
/// thinking (<c>{type: "enabled", budget_tokens}</c>) and reject <c>adaptive</c>, while the newer ones
/// reject <c>enabled</c>; and <c>output_config.effort</c> is refused by models without effort support.
/// GET /v1/models reports all of this, so it is used when available and <see cref="Guess"/> fills in
/// for a model id the list hasn't been fetched for (or doesn't contain).
/// </summary>
public sealed record AnthropicModelInfo(
    string Id,
    string? DisplayName,
    int? MaxOutputTokens,
    bool SupportsAdaptiveThinking,
    bool SupportsManualThinking,
    bool SupportsEffort,
    IReadOnlySet<string>? EffortLevels)
{
    public bool SupportsEffortLevel(string level) =>
        SupportsEffort && (EffortLevels is null || EffortLevels.Contains(level));

    // claude-3*, and the Claude 4 generations up to 4.5 (dated or alias ids): manual thinking only.
    private static readonly Regex ManualThinkingOnly = new(
        @"^claude-(3|(opus|sonnet|haiku)-4(-[015])?(-\d{8})?$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A best guess from the id alone: anything not recognisably older than Claude Opus 4.6
    /// is treated as a current model (adaptive thinking, effort), since new models keep that shape.</summary>
    public static AnthropicModelInfo Guess(string id)
    {
        if (!ManualThinkingOnly.IsMatch(id))
        {
            return new AnthropicModelInfo(id, null, null, true, false, true, null);
        }

        int? maxOutput = id.StartsWith("claude-3-5", StringComparison.OrdinalIgnoreCase) ? 8192
            : id.StartsWith("claude-3", StringComparison.OrdinalIgnoreCase) ? 4096
            : null;
        var effort = id.StartsWith("claude-opus-4-5", StringComparison.OrdinalIgnoreCase);
        var thinking = !id.StartsWith("claude-3-", StringComparison.OrdinalIgnoreCase)
                       || id.StartsWith("claude-3-7", StringComparison.OrdinalIgnoreCase);
        return new AnthropicModelInfo(id, null, maxOutput, false, thinking, effort, null);
    }

    /// <summary>Parses one entry of GET /v1/models' <c>data</c> array. Null for an entry without an
    /// id. Capabilities the entry doesn't report fall back to <see cref="Guess"/>.</summary>
    public static AnthropicModelInfo? Parse(JsonNode? model)
    {
        if (model?["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || id.Length == 0)
        {
            return null;
        }

        var guess = Guess(id);
        var capabilities = model["capabilities"];
        var thinkingTypes = capabilities?["thinking"]?["types"];
        var effort = capabilities?["effort"];

        HashSet<string>? effortLevels = null;
        if (effort is JsonObject effortObject)
        {
            effortLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in effortObject)
            {
                if (name != "supported" && IsSupported(value) == true)
                {
                    effortLevels.Add(name);
                }
            }
        }

        return new AnthropicModelInfo(
            id,
            model["display_name"]?.GetValue<string>(),
            ReadInt(model["max_tokens"]) ?? guess.MaxOutputTokens,
            IsSupported(thinkingTypes?["adaptive"]) ?? guess.SupportsAdaptiveThinking,
            IsSupported(thinkingTypes?["enabled"]) ?? guess.SupportsManualThinking,
            IsSupported(effort) ?? guess.SupportsEffort,
            effortLevels is { Count: > 0 } ? effortLevels : null);
    }

    /// <summary>Capabilities are reported as <c>{"supported": bool}</c> objects; a bare bool is
    /// accepted too, in case the shape is ever flattened.</summary>
    private static bool? IsSupported(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonObject obj when obj["supported"] is JsonValue supported && supported.TryGetValue<bool>(out var flag) => flag,
        _ => null,
    };

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number > 0 ? number : null;
}
