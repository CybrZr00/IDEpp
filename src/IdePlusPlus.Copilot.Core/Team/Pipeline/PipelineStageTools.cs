using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// The tool each stage's agents hand their result in with - one per kind of artifact. An agent's result
/// is only ever a call to its submit tool: the runner checks it, writes the artifact, and nothing the
/// agent says in the conversation is kept. (The orchestrator's own are <see cref="PipelineLeadTools"/>.)
/// </summary>
public static class PipelineStageTools
{
    public const string SurveySubmitToolName = "survey_submit";
    public const string DesignSubmitToolName = "design_submit";
    public const string BuildSubmitToolName = "build_submit";

    public static readonly ToolDefinition SurveySubmit = new(
        Name: SurveySubmitToolName,
        Description: "Hand in your survey. Call this once, when you have read enough of your area. List what already exists " +
                     "that the goal can reuse (each with the file that shows it), what does not exist yet but the goal needs " +
                     "(each with why), and what looks dead, duplicated or over-built (each with the evidence: who calls it, " +
                     "how many references). Every item names a file. A trim is a recommendation and nothing more.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "area": { "type": "string", "description": "Optional: the area you surveyed. Defaults to the one you were given." },
                "summary": { "type": "string", "description": "One or two sentences: what you found, overall." },
                "exists": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "pattern": { "type": "string", "description": "A pattern, helper or convention the goal can reuse." },
                      "file": { "type": "string", "description": "The file that shows it, relative to the solution folder." },
                      "note": { "type": "string", "description": "Optional: how to use it, or a caveat." }
                    },
                    "required": ["pattern", "file"]
                  }
                },
                "needs": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "what": { "type": "string", "description": "What does not exist yet that the goal needs." },
                      "rationale": { "type": "string", "description": "Why the goal needs it." },
                      "file": { "type": "string", "description": "Optional: where it would go or what it would touch." }
                    },
                    "required": ["what", "rationale"]
                  }
                },
                "trims": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "what": { "type": "string", "description": "What looks dead, duplicated or over-built." },
                      "file": { "type": "string", "description": "Where it is, relative to the solution folder." },
                      "evidence": { "type": "string", "description": "Who calls it, how many references - what you checked." }
                    },
                    "required": ["what", "file", "evidence"]
                  }
                }
              }
            }
            """)!);

    public static readonly ToolDefinition DesignSubmit = new(
        Name: DesignSubmitToolName,
        Description: "Hand in your design. Call this once, after you have committed the interface stubs (team_commit) and the " +
                     "solution builds. List every file the work will create or change, each with exactly one owner, and mark " +
                     "the files you wrote as stubs with isStub - your commit may change those and no others. The runner checks " +
                     "the design and the commit, builds it, and lands the commit on the team branch for the owners to build " +
                     "against; if anything is wrong it says what, and you fix it and call this again.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "summary": { "type": "string", "description": "What is being built and how the pieces fit together, in a few sentences." },
                "owners": { "type": "array", "items": { "type": "string" }, "description": "The owners, named b1, b2, ... in order, with no gaps." },
                "files": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "path": { "type": "string", "description": "Relative to the solution folder, with forward slashes. Include files that do not exist yet." },
                      "owner": { "type": "string", "description": "The one owner who edits this file, e.g. \"b2\"." },
                      "purpose": { "type": "string", "description": "What the file is for." },
                      "acceptance": { "type": "array", "items": { "type": "string" }, "description": "What an owner and a judge can check to know the file is right." },
                      "isStub": { "type": "boolean", "description": "True for a file you wrote in your contract commit: an interface or signature whose bodies throw NotImplementedException." }
                    },
                    "required": ["path", "owner", "purpose", "acceptance"]
                  }
                },
                "misuseCases": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string", "description": "M1, M2, ..." },
                      "scenario": { "type": "string", "description": "Something a careless or hostile user could try: empty, huge or malformed input, two at once, the wrong user." }
                    },
                    "required": ["id", "scenario"]
                  }
                },
                "trims": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "what": { "type": "string" },
                      "file": { "type": "string" },
                      "reason": { "type": "string", "description": "Why the goal needs it trimmed. Leave out any trim the goal does not need." }
                    },
                    "required": ["what", "file", "reason"]
                  }
                }
              },
              "required": ["summary", "owners", "files"]
            }
            """)!);

    public static readonly ToolDefinition BuildSubmit = new(
        Name: BuildSubmitToolName,
        Description: "Report that you are done building your files. Call this once, when every file you own meets its " +
                     "acceptance criteria and everything is merged to the team branch - work that is not merged is lost. " +
                     "Say what you built, and list anything you could not do or that depends on a file you do not own. " +
                     "If you still have unmerged work the runner tells you so once; merge it, or say why you cannot.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "summary": { "type": "string", "description": "What you built, in a sentence or two." },
                "unmet": { "type": "array", "items": { "type": "string" }, "description": "Optional: what you could not do, and why - one short item each." }
              },
              "required": ["summary"]
            }
            """)!);

    /// <summary>What a surveyor has: the read-only tools every sub-agent has, and its submit tool. Nothing
    /// that writes, and no commands.</summary>
    public static IReadOnlyList<ToolDefinition> ForSurveyor { get; } = [.. SubAgentTools.AllowedTools, SurveySubmit];

    /// <summary>
    /// The tools of an owner's worktree dispatcher that it is given, by name: read, edit (its own files - the
    /// dispatcher enforces which), build, test, commit, bring the team branch in and merge to it, and run a command
    /// when the run allows one. Not the board or the team channel: nothing is written to the board, and what an owner
    /// needs from another owner goes through the runner's own tools.
    /// </summary>
    public static IReadOnlySet<string> OwnerToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkspaceTools.ReadFileToolName,
        WorkspaceTools.ListFilesToolName,
        WorkspaceTools.SearchCodebaseToolName,
        SymbolTools.FindSymbolToolName,
        SymbolTools.FindReferencesToolName,
        DiagnosticTools.GetDiagnosticsToolName,
        TeamTools.EditFileToolName,
        GitTools.StatusToolName,
        GitTools.DiffToolName,
        BuildTools.BuildToolName,
        TestTools.RunTestsToolName,
        ShellTools.RunCommandToolName,
        TeamTools.CommitToolName,
        TeamTools.SyncToolName,
        TeamTools.MergeToolName,
    };

    /// <summary>
    /// The tools of the designer's worktree dispatcher that it is given, by name: read the code, edit,
    /// build, look at the state of its branch, and commit. Not team_merge (the runner lands its commit), not
    /// the team's chatter, not the tests. The dispatcher defines these; this is which of them the designer sees.
    /// </summary>
    public static IReadOnlySet<string> DesignerToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkspaceTools.ReadFileToolName,
        WorkspaceTools.ListFilesToolName,
        WorkspaceTools.SearchCodebaseToolName,
        SymbolTools.FindSymbolToolName,
        SymbolTools.FindReferencesToolName,
        DiagnosticTools.GetDiagnosticsToolName,
        TeamTools.EditFileToolName,
        GitTools.StatusToolName,
        GitTools.DiffToolName,
        BuildTools.BuildToolName,
        TeamTools.CommitToolName,
    };
}
