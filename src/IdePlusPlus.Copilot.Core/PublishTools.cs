using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The publish tool. Definition only - the handler lives in the chat pane, alongside PublishService,
/// the profile store and the editor documents that have to be written to disk first, exactly as
/// <see cref="BuildTools"/> does.
///
/// Two restrictions are deliberate, and both are stated in the description so the model doesn't
/// waste a turn discovering them:
///
/// It can run an existing profile but not create or edit one. A publish profile is a deployment
/// configuration; deciding what gets shipped, where, and how is a human decision, and one the user
/// makes in a dialog built for it.
///
/// It can only run folder profiles. A container profile with a registry configured *pushes an image
/// to that registry* - an outward-facing action with no undo. This codebase's model for those is
/// explicit per-action user confirmation (run_command, MCP trust, fetch_url), and until publish has
/// something equivalent, container publishes stay user-initiated from the dialog.
/// </summary>
public static class PublishTools
{
    public const string PublishToolName = "publish_project";

    public const int MaxRawOutputChars = 4_000;

    public static bool IsPublishTool(string toolName) => toolName is PublishToolName;

    public static readonly ToolDefinition Publish = new(
        Name: PublishToolName,
        Description: "Publish a project using one of its existing publish profiles, and return " +
                     "whether it succeeded. The profile must already exist in the project's " +
                     "Properties/PublishProfiles folder - this tool cannot create or change one, so " +
                     "if the profile the user wants does not exist yet, say so and point them at " +
                     "Build > Publish Settings rather than trying to write the file yourself. Only " +
                     "folder profiles can be run here; a container profile has to be published by " +
                     "the user from the Publish dialog, because it can push an image to a remote " +
                     "registry. Files with unsaved changes are written to disk first, so the publish " +
                     "reflects the edits you have made. Uses the configuration the profile specifies, " +
                     "not the one selected in the IDE.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "profile": { "type": "string", "description": "Name of the publish profile to run, without the .pubxml extension. Omit to be told which profiles the project actually has." },
                "project": { "type": "string", "description": "Path to the .csproj, relative to the solution directory. Omit only when the solution has a single project." }
              }
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [Publish];
}
