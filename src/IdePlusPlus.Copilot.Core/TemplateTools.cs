using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The template tools. Definitions only - the handlers live in the chat pane, alongside the template
/// catalog and the solution, exactly as <see cref="PublishTools"/> does.
///
/// <para>Three restrictions are deliberate, and all three are stated in the descriptions so the model
/// does not spend a turn discovering them.</para>
///
/// <para><b>No template-package installing.</b> A template package is arbitrary code fetched by name
/// from a public feed that later runs as a template on the user's machine. That is the same shape as
/// the run_command and MCP-trust prompts elsewhere here, and it stays user-initiated from Tools &gt;
/// Template Packages.</para>
///
/// <para><b>No solution templates.</b> aspire-starter and friends emit their own solution file and
/// several projects - running one does not add to the open solution, it produces a different one to
/// open instead. Replacing what the user has open is not a side effect to have unattended.</para>
///
/// <para><b>Everything stays inside the open solution's directory.</b> The model chooses the paths,
/// so they are contained the same way read_file's are.</para>
/// </summary>
public static class TemplateTools
{
    public const string ListTemplatesToolName = "list_templates";

    public const string CreateProjectToolName = "create_project";

    public const string AddItemToolName = "add_item";

    public static bool IsTemplateTool(string toolName) =>
        toolName is ListTemplatesToolName or CreateProjectToolName or AddItemToolName;

    public static readonly ToolDefinition ListTemplates = new(
        Name: ListTemplatesToolName,
        Description: "List the .NET templates installed on this machine, with the short name each " +
                     "one is created by. Use this before create_project or add_item rather than " +
                     "guessing a short name - what is installed varies by machine and by SDK " +
                     "version. Read-only.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["project", "item"], "description": "Which kind to list. Omit for both." },
                "search": { "type": "string", "description": "Filter across names, short names, descriptions and tags. Omit to list everything." }
              }
            }
            """)!);

    public static readonly ToolDefinition CreateProject = new(
        Name: CreateProjectToolName,
        Description: "Create a new project from a template and add it to the open solution. The " +
                     "template's short name must be one list_templates reports as a project " +
                     "template - solution templates (Aspire starters, MAUI multi-project) are " +
                     "refused here, because they bring their own solution file and would replace " +
                     "the one that is open; tell the user to use File > New > Project for those. " +
                     "The project is created inside the solution's directory. Cannot install a " +
                     "template package that is not present - say so and point the user at Tools > " +
                     "Template Packages instead.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "required": ["template", "name"],
              "properties": {
                "template": { "type": "string", "description": "The template's short name, e.g. classlib or xunit." },
                "name": { "type": "string", "description": "Name for the new project. Becomes its directory and assembly name." },
                "framework": { "type": "string", "description": "Target framework, e.g. net10.0. Omit for the template's default." },
                "solutionFolder": { "type": "string", "description": "Solution folder to place it under, e.g. src. Omit for the solution root." }
              }
            }
            """)!);

    public static readonly ToolDefinition AddItem = new(
        Name: AddItemToolName,
        Description: "Add one file to an existing project from an item template - a class, " +
                     "interface, record, API controller and so on. Prefer this over writing such a " +
                     "file with propose_edit: it uses the same templates the IDE and the dotnet CLI " +
                     "use, so the result matches the project's language version and implicit " +
                     "usings, and the namespace is set from the project and the target folder. The " +
                     "folder is created if it does not exist, and must be inside the project.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "required": ["template", "name"],
              "properties": {
                "template": { "type": "string", "description": "The item template's short name, e.g. class, interface, record, apicontroller." },
                "name": { "type": "string", "description": "Name for the new file, without extension. Becomes the type name." },
                "folder": { "type": "string", "description": "Folder for the file, relative to the project directory, e.g. Models. Omit for the project root." },
                "project": { "type": "string", "description": "Path to the .csproj, relative to the solution directory. Omit only when the solution has a single project." }
              }
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [ListTemplates, CreateProject, AddItem];
}
