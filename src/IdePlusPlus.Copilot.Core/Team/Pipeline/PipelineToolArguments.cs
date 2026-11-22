using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>Reads what a model sent to a pipeline tool.</summary>
public static class PipelineToolArguments
{
    /// <summary>
    /// A tool's arguments read as <typeparamref name="T"/> (property names in any case, numbers as strings
    /// accepted - see <see cref="PipelineJson"/>), or why they could not be. The reason is meant to be
    /// returned to the model: it has called the tool with the wrong shape, and told only that it failed it
    /// would send the same thing again.
    /// </summary>
    public static PipelineCheck<T> Parse<T>(JsonNode? arguments, string tool) where T : class
    {
        try
        {
            return arguments?.Deserialize<T>(PipelineJson.Options) is { } value
                ? PipelineCheck<T>.Ok(value)
                : PipelineCheck<T>.Failed([$"{tool} needs its arguments - see the tool's description for their shape."]);
        }
        catch (JsonException ex)
        {
            var reason = PipelineText.Truncate(PipelineText.Clean(ex.Message), 200, out _);
            return PipelineCheck<T>.Failed([$"{tool}'s arguments could not be read: {reason} Check them against the tool's description and call it again."]);
        }
    }
}
