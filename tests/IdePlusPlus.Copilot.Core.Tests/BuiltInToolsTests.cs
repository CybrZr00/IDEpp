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
    public void All_IncludesEveryBuiltInToolFamily()
    {
        var names = BuiltInTools.All.Select(t => t.Name).ToList();

        names.ShouldContain(WorkspaceTools.ReadFileToolName);
        names.ShouldContain(WorkspaceTools.ListFilesToolName);
        names.ShouldContain(WorkspaceTools.SearchCodebaseToolName);
        names.ShouldContain(GitTools.DiffToolName);
        names.ShouldContain(GitTools.StatusToolName);
        names.ShouldContain(BuildTools.BuildToolName);
        names.ShouldContain(PublishTools.PublishToolName);
        names.ShouldContain(TestTools.RunTestsToolName);
        names.ShouldContain(DiagnosticTools.GetDiagnosticsToolName);
        names.ShouldContain(SymbolTools.FindSymbolToolName);
        names.ShouldContain(SymbolTools.FindReferencesToolName);
        names.ShouldContain(SkillTools.ReadSkillToolName);
        names.ShouldContain(SubAgentTools.InvestigateParallelToolName);
        names.ShouldContain(TemplateTools.ListTemplatesToolName);
        names.ShouldContain(TemplateTools.CreateProjectToolName);
        names.ShouldContain(TemplateTools.AddItemToolName);
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

    /// <summary>run_command is opt-in, so it must be absent from the always-offered list but still
    /// route as built-in - otherwise a call made while it was enabled would be forwarded to MCP as
    /// an unknown tool.</summary>
    [Fact]
    public void OptInTools_AreRoutableButNotAlwaysOffered()
    {
        BuiltInTools.All.ShouldNotContain(t => t.Name == ShellTools.RunCommandToolName);
        BuiltInTools.OptIn.ShouldContain(t => t.Name == ShellTools.RunCommandToolName);
        BuiltInTools.IsBuiltIn(ShellTools.RunCommandToolName).ShouldBeTrue();
    }

    [Fact]
    public void OptInTools_HaveDescriptionsAndObjectSchemasToo()
    {
        foreach (var tool in BuiltInTools.OptIn)
        {
            tool.Description.ShouldNotBeNullOrWhiteSpace();
            tool.ParametersSchema["type"]!.GetValue<string>().ShouldBe("object");
        }
    }

    /// <summary>fetch_url is gated by its own AreWebFetchEnabled toggle, independent of OptIn's
    /// AreShellCommandsEnabled - so it lives in its own list, but must still route as built-in for
    /// the same reason run_command must.</summary>
    [Fact]
    public void WebOptInTools_AreRoutableButNotAlwaysOffered()
    {
        BuiltInTools.All.ShouldNotContain(t => t.Name == WebTools.FetchUrlToolName);
        BuiltInTools.WebOptIn.ShouldContain(t => t.Name == WebTools.FetchUrlToolName);
        BuiltInTools.IsBuiltIn(WebTools.FetchUrlToolName).ShouldBeTrue();
    }

    [Fact]
    public void WebOptInTools_HaveDescriptionsAndObjectSchemasToo()
    {
        foreach (var tool in BuiltInTools.WebOptIn)
        {
            tool.Description.ShouldNotBeNullOrWhiteSpace();
            tool.ParametersSchema["type"]!.GetValue<string>().ShouldBe("object");
        }
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
