using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>
/// AllowedTools is a hard-coded allow-list specifically so a sub-agent's read-only guarantee stays
/// true as the rest of the tool surface grows - these tests guard that invariant directly, since a
/// silent addition here would be exactly the kind of change nothing else would catch.
/// </summary>
public class SubAgentToolsTests
{
    [Theory]
    [InlineData(WorkspaceTools.ReadFileToolName)]
    [InlineData(WorkspaceTools.ListFilesToolName)]
    [InlineData(WorkspaceTools.SearchCodebaseToolName)]
    [InlineData(SymbolTools.FindSymbolToolName)]
    [InlineData(SymbolTools.FindReferencesToolName)]
    [InlineData(DiagnosticTools.GetDiagnosticsToolName)]
    public void AllowedTools_IncludesEveryExpectedReadOnlyTool(string toolName)
    {
        SubAgentTools.AllowedTools.ShouldContain(t => t.Name == toolName);
    }

    [Fact]
    public void AllowedTools_HasExactlyTheExpectedCount()
    {
        // Guards against a silent addition just as much as a silent removal - if this needs to
        // change, it should change here deliberately, with the count updated alongside it.
        SubAgentTools.AllowedTools.Count.ShouldBe(6);
    }

    [Theory]
    [InlineData("propose_edit")]
    [InlineData("propose_plan")]
    [InlineData(ShellTools.RunCommandToolName)]
    [InlineData(WebTools.FetchUrlToolName)]
    [InlineData(SaveTools.SaveFilesToolName)]
    [InlineData(BuildTools.BuildToolName)]
    [InlineData(TestTools.RunTestsToolName)]
    [InlineData(GitTools.DiffToolName)]
    [InlineData(SubAgentTools.InvestigateParallelToolName)]
    public void AllowedTools_ExcludesAnythingNotReadOnlyOrThatCouldRecurse(string toolName)
    {
        SubAgentTools.AllowedTools.ShouldNotContain(t => t.Name == toolName);
    }

    [Fact]
    public void InvestigateParallel_IsAdvertisedAndRoutesAsBuiltIn()
    {
        BuiltInTools.All.ShouldContain(t => t.Name == SubAgentTools.InvestigateParallelToolName);
        BuiltInTools.IsBuiltIn(SubAgentTools.InvestigateParallelToolName).ShouldBeTrue();
    }
}
