using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// The one set of JSON options for everything the pipeline writes to its run folder and everything a
/// model submits through a pipeline tool: camelCase, enums as words, nulls left out, and tolerant on
/// the way in (any property-name casing, comments and trailing commas skipped, numbers accepted as
/// strings) because the other end is a language model that is usually right about the shape and
/// occasionally a little off about the punctuation.
/// </summary>
public static class PipelineJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
