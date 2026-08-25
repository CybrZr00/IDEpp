using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Symbol search over the loaded C# solution. Definitions only - the handler lives in the chat pane
/// alongside the Roslyn workspace.
///
/// These replace guesswork. Without them the only way to find where something is used is
/// search_codebase, which is a similarity search and will happily miss a call site whose
/// surrounding text reads nothing like the query - and the cost of missing one is a change that
/// compiles and breaks a caller nobody looked at.
/// </summary>
public static class SymbolTools
{
    public const string FindSymbolToolName = "find_symbol";
    public const string FindReferencesToolName = "find_references";

    public const int MaxResults = 60;

    public static bool IsSymbolTool(string toolName) =>
        toolName is FindSymbolToolName or FindReferencesToolName;

    public static readonly ToolDefinition FindSymbol = new(
        Name: FindSymbolToolName,
        Description: "Find where a C# type, method, property or field is declared, by name. Exact and " +
                     "case-sensitive on the symbol's own name - pass \"GetStatusAsync\", not the fully-qualified " +
                     "name. Prefer this over search_codebase when you know what something is called: this is a " +
                     "compiler-accurate answer where search_codebase is a similarity search.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The symbol's own name, without namespace or type qualification." }
              },
              "required": ["name"]
            }
            """)!);

    public static readonly ToolDefinition FindReferences = new(
        Name: FindReferencesToolName,
        Description: "Find every place a C# symbol is used, by name. Use it before changing or removing anything " +
                     "shared - a signature change that compiles where you edited can still break callers you never " +
                     "read. If the name is ambiguous the declarations are listed instead, so you can pick one.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The symbol's own name, without namespace or type qualification." }
              },
              "required": ["name"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [FindSymbol, FindReferences];
}
