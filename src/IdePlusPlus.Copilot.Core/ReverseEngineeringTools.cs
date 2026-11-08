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

    // ---- Native (Ghidra-backed) tools ----

    public const string OpenToolName = "re_open";
    public const string CloseToolName = "re_close";
    public const string OverviewToolName = "re_overview";
    public const string FunctionsToolName = "re_functions";
    public const string DecompileToolName = "re_decompile";
    public const string XrefsToolName = "re_xrefs";
    public const string CallGraphToolName = "re_callgraph";
    public const string RenameToolName = "re_rename";
    public const string CommentToolName = "re_comment";

    private const string TargetDescription = "A function name, or its entry address as hex (e.g. 00401000).";

    public static readonly ToolDefinition Open = new(
        Name: OpenToolName,
        Description: "Open a native binary (.exe/.dll/.so/.dylib/...) in Ghidra. The first open imports and " +
                     "analyzes it, which can take a while; later ones are fast. Call this before the other native " +
                     "re_ tools. Needs Ghidra installed (Settings - Reverse engineering tools).",
        ParametersSchema: PathOnlySchema);

    public static readonly ToolDefinition Close = new(
        Name: CloseToolName,
        Description: "Close the currently open native binary.",
        ParametersSchema: EmptySchema);

    public static readonly ToolDefinition Overview = new(
        Name: OverviewToolName,
        Description: "Summarize the open native binary: name, language/architecture, compiler, image base, function and symbol counts.",
        ParametersSchema: EmptySchema);

    public static readonly ToolDefinition Functions = new(
        Name: FunctionsToolName,
        Description: "List functions in the open native binary, optionally filtered by a case-insensitive substring of the name.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "filter": { "type": "string", "description": "Optional case-insensitive substring of the function name." },
                "limit": { "type": "integer", "description": "Maximum functions to return (default 200)." }
              }
            }
            """)!);

    public static readonly ToolDefinition Decompile = new(
        Name: DecompileToolName,
        Description: "Decompile one function of the open native binary to C.",
        ParametersSchema: TargetSchema("The function to decompile. " + TargetDescription));

    public static readonly ToolDefinition Xrefs = new(
        Name: XrefsToolName,
        Description: "List references to a function of the open native binary (who calls or points at it).",
        ParametersSchema: TargetSchema(TargetDescription));

    public static readonly ToolDefinition CallGraph = new(
        Name: CallGraphToolName,
        Description: "List the functions that call, and are called by, a function of the open native binary.",
        ParametersSchema: TargetSchema(TargetDescription));

    public static readonly ToolDefinition Rename = new(
        Name: RenameToolName,
        Description: "Rename a function of the open native binary. The change is saved to the Ghidra project and persists.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "target": { "type": "string", "description": "A function name, or its entry address as hex." },
                "name": { "type": "string", "description": "The new name." }
              },
              "required": ["target", "name"]
            }
            """)!);

    public static readonly ToolDefinition Comment = new(
        Name: CommentToolName,
        Description: "Set a comment on a function of the open native binary. Saved to the Ghidra project and persists.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "target": { "type": "string", "description": "A function name, or its entry address as hex." },
                "comment": { "type": "string", "description": "The comment text." }
              },
              "required": ["target", "comment"]
            }
            """)!);

    /// <summary>The managed (.NET) reverse-engineering tools - in-process, no toolchain needed.</summary>
    public static IReadOnlyList<ToolDefinition> Managed { get; } = [ManagedOverview, ManagedTypes, ManagedDecompile];

    /// <summary>The native (Ghidra-backed) reverse-engineering tools.</summary>
    public static IReadOnlyList<ToolDefinition> Native { get; } =
        [Open, Close, Overview, Functions, Decompile, Xrefs, CallGraph, Rename, Comment];

    /// <summary>Every reverse-engineering tool.</summary>
    public static IReadOnlyList<ToolDefinition> All { get; } = [.. Managed, .. Native];

    public static bool IsReverseEngineeringTool(string name) => All.Any(tool => tool.Name == name);

    private static JsonNode PathOnlySchema => JsonNode.Parse("""
        { "type": "object", "properties": { "path": { "type": "string", "description": "Path to the binary, absolute or relative to the solution." } }, "required": ["path"] }
        """)!;

    private static JsonNode EmptySchema => JsonNode.Parse("""{ "type": "object", "properties": {} }""")!;

    private static JsonNode TargetSchema(string description) => JsonNode.Parse($$"""
        { "type": "object", "properties": { "target": { "type": "string", "description": {{System.Text.Json.JsonSerializer.Serialize(description)}} } }, "required": ["target"] }
        """)!;
}
