using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>The owners' change-request tools: what they are called, what each requires, how what comes back is read, and
/// which files an owner may claim.</summary>
public sealed class PipelineChangeToolsTests
{
    [Fact]
    public void TheSixTools_HaveTheirOwnNames_AndAreRecognisedAsChangeTools()
    {
        PipelineChangeTools.All.Select(t => t.Name).ShouldBe(
            ["request_change", "await_change", "respond_change", "change_done", "appeal_change", "claim_new_file"]);
        PipelineChangeTools.All.ShouldAllBe(t => PipelineChangeTools.IsChangeTool(t.Name));
        PipelineChangeTools.IsChangeTool("build_submit").ShouldBeFalse();
        PipelineChangeTools.IsChangeTool("team_merge").ShouldBeFalse();
    }

    [Theory]
    [InlineData("request_change", new[] { "file", "change", "rationale" })]
    [InlineData("await_change", new[] { "id" })]
    [InlineData("respond_change", new[] { "id", "decision" })]
    [InlineData("change_done", new[] { "id" })]
    [InlineData("appeal_change", new[] { "id", "argument" })]
    [InlineData("claim_new_file", new[] { "path", "reason" })]
    public void EachToolRequiresWhatItCannotDoWithout(string tool, string[] required)
    {
        var schema = PipelineChangeTools.All.Single(t => t.Name == tool).ParametersSchema;

        schema["type"]!.GetValue<string>().ShouldBe("object");
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(required);
    }

    [Fact]
    public void TheDecisionIsOneOfTwoWords()
    {
        var decision = PipelineChangeTools.RespondChange.ParametersSchema["properties"]!["decision"]!["enum"]!;

        decision.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["accept", "reject"]);
    }

    [Fact]
    public void TheArgumentsAreReadAsTheModelSendsThem_NumbersAsTextIncluded()
    {
        var wait = PipelineToolArguments.Parse<AwaitChangeArguments>(JsonNode.Parse("""{ "id": "CR3", "minutes": "7" }"""), "await_change");
        var request = PipelineToolArguments.Parse<RequestChangeArguments>(
            JsonNode.Parse("""{ "file": "Lib/A.cs", "change": "c", "rationale": "r" }"""), "request_change");
        var respond = PipelineToolArguments.Parse<RespondChangeArguments>(
            JsonNode.Parse("""{ "id": "CR1", "decision": "reject", "reasoning": "No." }"""), "respond_change");

        wait.Value.ShouldBe(new AwaitChangeArguments("CR3", 7));
        request.Value.ShouldBe(new RequestChangeArguments("Lib/A.cs", "c", "r"));
        respond.Value.ShouldBe(new RespondChangeArguments("CR1", "reject", "No."));
    }

    [Fact]
    public void TheWaitHasABoundedRange()
    {
        PipelineChangeTools.MaxAwaitMinutes.ShouldBe(10);
        PipelineChangeTools.DefaultAwaitMinutes.ShouldBeLessThanOrEqualTo(PipelineChangeTools.MaxAwaitMinutes);
    }

    // ---- Which files may be claimed ----

    [Theory]
    [InlineData("Lib/Lib.csproj")]
    [InlineData("App/App.CSPROJ")]
    [InlineData("Scratch.slnx")]
    [InlineData("All.sln")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/Common.targets")]
    [InlineData("global.json")]
    [InlineData("NuGet.Config")]
    [InlineData("Lib/packages.lock.json")]
    public void AFileThatDefinesTheBuild_CannotBeClaimed(string path)
    {
        BuildDefiningFiles.Refusal(path).ShouldNotBeNull().ShouldContain("defines how the solution builds");
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("Lib/.git/hooks/pre-commit")]
    public void AnythingInsideGit_CannotBeClaimed(string path)
    {
        BuildDefiningFiles.Refusal(path).ShouldNotBeNull().ShouldContain("belongs to git");
    }

    [Theory]
    [InlineData("Lib/Greeter.cs")]
    [InlineData("Tests/GreeterTests.cs")]
    [InlineData("docs/greeter.md")]
    [InlineData("Lib/.gitignore")]
    [InlineData("Lib/Properties.cs")]
    public void AnOrdinaryNewFile_CanBeClaimed(string path)
    {
        BuildDefiningFiles.Refusal(path).ShouldBeNull();
    }
}
