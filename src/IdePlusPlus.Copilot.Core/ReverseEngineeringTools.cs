using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The reverse-engineering tools. They are opt-in (gated by a toggle, off by default) because they
/// act on binaries the user points at. The managed (.NET) tools here read IL and metadata in-process
/// and never load or run the target; the native tools (Ghidra-backed) are added in a later phase.
/// </summary>
public static class ReverseEngineeringTools
{
    public const string ManagedOverviewToolName = "re_managed_overview";
    public const string ManagedTypesToolName = "re_managed_types";
    public const string ManagedDecompileToolName = "re_managed_decompile";

    public const int MaxOutputChars = 60_000;

    public static readonly ToolDefinition ManagedOverview = new(
        Name: ManagedOverviewToolName,
        Description: "Summarize a .NET assembly (.dll or .exe): its name, version, type count and referenced " +
                     "assemblies. Reads metadata only - the assembly is never loaded or executed.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Path to a managed .dll/.exe, absolute or relative to the solution." }
              },
              "required": ["path"]
            }
            """)!);

    public static readonly ToolDefinition ManagedTypes = new(
        Name: ManagedTypesToolName,
        Description: "List the types in a .NET assembly, optionally filtered by a case-insensitive substring of the full type name.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Path to a managed .dll/.exe." },
                "filter": { "type": "string", "description": "Optional case-insensitive substring of the full type name." }
              },
              "required": ["path"]
            }
            """)!);

    public static readonly ToolDefinition ManagedDecompile = new(
        Name: ManagedDecompileToolName,
        Description: "Decompile one type of a .NET assembly back to C#. Reads IL/metadata in-process; the assembly is never executed.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Path to a managed .dll/.exe." },
                "type": { "type": "string", "description": "The type to decompile, by full name (Namespace.Type) or simple name." }
              },
              "required": ["path", "type"]
            }
            """)!);

    /// <summary>The managed (.NET) reverse-engineering tools.</summary>
    public static IReadOnlyList<ToolDefinition> Managed { get; } = [ManagedOverview, ManagedTypes, ManagedDecompile];

    /// <summary>Every reverse-engineering tool (native tools are added in a later phase).</summary>
    public static IReadOnlyList<ToolDefinition> All { get; } = [.. Managed];

    public static bool IsReverseEngineeringTool(string name) => All.Any(tool => tool.Name == name);
}
