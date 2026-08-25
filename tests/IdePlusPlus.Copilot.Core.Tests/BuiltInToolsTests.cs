using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>
/// Guards the invariant that made adding a tool error-prone: the list advertised to the model and
/// the predicate that routes an incoming call have to agree. When they disagreed, the tool was
/// offered, called, and then forwarded to the MCP server manager, which answered "unknown tool" -
/// a failure that looks like the model misbehaving rather than a missing line of wiring.
/// </summary>
public class BuiltInToolsTests
{
    [Fact]
    public void EveryAdvertisedTool_RoutesAsBuiltIn()
    {
        foreach (var tool in BuiltInTools.All)
        {
            BuiltInTools.IsBuiltIn(tool.Name).ShouldBeTrue($"'{tool.Name}' is offered but would route to MCP");
        }
    }

    [Fact]
    public void All_IncludesTheWorkspaceGitAndBuildTools()
    {
        var names = BuiltInTools.All.Select(t => t.Name).ToList();

        names.ShouldContain(WorkspaceTools.ReadFileToolName);
        names.ShouldContain(WorkspaceTools.ListFilesToolName);
        names.ShouldContain(WorkspaceTools.SearchCodebaseToolName);
        names.ShouldContain(GitTools.DiffToolName);
        names.ShouldContain(GitTools.StatusToolName);
        names.ShouldContain(BuildTools.BuildToolName);
    }

    [Fact]
    public void ToolNames_AreUnique()
    {
        var names = BuiltInTools.All.Select(t => t.Name).ToList();

        names.Distinct().Count().ShouldBe(names.Count);
    }

    /// <summary>propose_edit is routed by name before this predicate is consulted, so it must not
    /// also be listed here.</summary>
    [Fact]
    public void All_DoesNotIncludeTheEditOrPlanTools()
    {
        var names = BuiltInTools.All.Select(t => t.Name).ToList();

        names.ShouldNotContain("propose_edit");
        names.ShouldNotContain("propose_plan");
    }

    [Fact]
    public void IsBuiltIn_ForAnMcpStyleQualifiedName_IsFalse()
    {
        BuiltInTools.IsBuiltIn("git__git_diff").ShouldBeFalse();
    }

    [Fact]
    public void EveryTool_HasADescriptionAndAnObjectSchema()
    {
        foreach (var tool in BuiltInTools.All)
        {
            tool.Description.ShouldNotBeNullOrWhiteSpace();
            tool.ParametersSchema["type"]!.GetValue<string>().ShouldBe("object");
        }
    }
}
