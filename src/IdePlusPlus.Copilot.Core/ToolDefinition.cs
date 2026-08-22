using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

public sealed record ToolDefinition(string Name, string Description, JsonNode ParametersSchema);
