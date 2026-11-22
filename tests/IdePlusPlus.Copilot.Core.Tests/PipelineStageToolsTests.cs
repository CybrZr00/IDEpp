using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>The surveyor's submit tool: what it offers a surveyor, and that what comes back through it
/// is read the way the survey validator expects.</summary>
public sealed class PipelineStageToolsTests
{
    [Fact]
    public void ASurveyor_HasTheReadingTools_AndItsSubmitTool_AndNothingThatWrites()
    {
        var names = PipelineStageTools.ForSurveyor.Select(t => t.Name).ToList();

        names.ShouldBe([.. SubAgentTools.AllowedTools.Select(t => t.Name), PipelineStageTools.SurveySubmitToolName]);
        names.ShouldNotContain("edit_file");
        names.ShouldNotContain("run_command");
        PipelineStageTools.SurveySubmitToolName.ShouldBe("survey_submit");
    }

    [Fact]
    public void TheSurveySchema_RequiresWhatEachItemNeedsToBeUseful_ButNothingOfTheSurveyItself()
    {
        var schema = PipelineStageTools.SurveySubmit.ParametersSchema;

        schema["required"].ShouldBeNull(); // an area is defaulted; a survey with nothing in it is refused by the validator
        Required(schema, "exists").ShouldBe(["pattern", "file"]);
        Required(schema, "needs").ShouldBe(["what", "rationale"]);
        Required(schema, "trims").ShouldBe(["what", "file", "evidence"]);
    }

    [Fact]
    public void ASurveyAsItsToolTakesIt_IsReadAndChecksOut()
    {
        var arguments = JsonNode.Parse("""
            {
              "summary": "One class, one property.",
              "exists": [ { "pattern": "Value property", "file": "Lib/Class1.cs", "note": "read-only" } ],
              "needs": [ { "what": "A setter", "rationale": "the goal changes the value", "file": "Lib/Class1.cs" } ],
              "trims": [ { "what": "Unused Helper", "file": "Lib/Helper.cs", "evidence": "0 references" } ]
            }
            """);

        var parsed = PipelineToolArguments.Parse<SurveyReport>(arguments, PipelineStageTools.SurveySubmitToolName);

        parsed.IsValid.ShouldBeTrue(parsed.Feedback);
        var check = SurveyValidator.Validate(parsed.Value! with { Area = "Lib" });
        check.IsValid.ShouldBeTrue(check.Feedback);
        check.Value!.Exists.ShouldHaveSingleItem().Note.ShouldBe("read-only");
        check.Value.Needs.ShouldHaveSingleItem().Rationale.ShouldBe("the goal changes the value");
        check.Value.Trims.ShouldHaveSingleItem().Evidence.ShouldBe("0 references");
    }

    [Fact]
    public void ASurveyWithNoArea_ReadsAsOneWithoutAnArea_ForTheRunnerToFillIn()
    {
        var parsed = PipelineToolArguments.Parse<SurveyReport>(
            JsonNode.Parse("""{ "exists": [ { "pattern": "p", "file": "a.cs" } ] }"""), PipelineStageTools.SurveySubmitToolName);

        parsed.IsValid.ShouldBeTrue(parsed.Feedback);
        parsed.Value!.Area.ShouldBeNullOrEmpty();
        SurveyValidator.Validate(parsed.Value).Feedback.ShouldContain("needs an area");
        SurveyValidator.Validate(parsed.Value with { Area = "Lib" }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void TheDesignSchema_RequiresTheSummaryOwnersAndFiles_AndWhatAFileNeeds()
    {
        var schema = PipelineStageTools.DesignSubmit.ParametersSchema;

        PipelineStageTools.DesignSubmitToolName.ShouldBe("design_submit");
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["summary", "owners", "files"]);
        Required(schema, "files").ShouldBe(["path", "owner", "purpose", "acceptance"]);
        Required(schema, "misuseCases").ShouldBe(["id", "scenario"]);
        Required(schema, "trims").ShouldBe(["what", "file", "reason"]);
        schema["properties"]!["waves"].ShouldBeNull(); // this run builds in one wave: the designer is not offered the choice
    }

    [Fact]
    public void TheDesigner_IsGivenToReadEditBuildAndCommit_ButNotToMergeOrTalk()
    {
        var names = PipelineStageTools.DesignerToolNames;

        names.ShouldContain("read_file");
        names.ShouldContain("edit_file");
        names.ShouldContain("build_solution");
        names.ShouldContain("team_commit");
        names.ShouldContain("git_status");
        names.ShouldNotContain("team_merge");
        names.ShouldNotContain("team_post");
        names.ShouldNotContain("team_dm");
        names.ShouldNotContain("run_command");
        names.ShouldNotContain("run_tests");
    }

    [Fact]
    public void ADesignAsItsToolTakesIt_IsReadAndChecksOut()
    {
        var arguments = JsonNode.Parse("""
            {
              "summary": "A greeter behind an interface.",
              "owners": ["b1", "b2"],
              "files": [
                { "path": "Lib/IGreeter.cs", "owner": "b1", "purpose": "The contract.", "acceptance": ["Has Greet(string)."], "isStub": true },
                { "path": "Lib/Greeter.cs", "owner": "b2", "purpose": "Implements it.", "acceptance": ["Greets by name."] }
              ],
              "misuseCases": [ { "id": "m1", "scenario": "A null name." } ],
              "trims": [ { "what": "Unused Helper", "file": "Lib/Helper.cs", "reason": "The goal replaces it." } ]
            }
            """);

        var parsed = PipelineToolArguments.Parse<DesignPlan>(arguments, PipelineStageTools.DesignSubmitToolName);

        parsed.IsValid.ShouldBeTrue(parsed.Feedback);
        var check = DesignValidator.Validate(parsed.Value, new DesignLimits());
        check.IsValid.ShouldBeTrue(check.Feedback);
        check.Value!.Files.Select(f => (f.Path, f.Owner, f.IsStub)).ShouldBe([("Lib/IGreeter.cs", "b1", true), ("Lib/Greeter.cs", "b2", false)]);
        check.Value.MisuseCases.ShouldHaveSingleItem().Id.ShouldBe("M1");
        check.Value.Waves.ShouldBe(1);
    }

    [Fact]
    public void TheBuildReportTool_RequiresOnlyASummary()
    {
        var schema = PipelineStageTools.BuildSubmit.ParametersSchema;

        PipelineStageTools.BuildSubmitToolName.ShouldBe("build_submit");
        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["summary"]);
        schema["properties"]!["unmet"]!["type"]!.GetValue<string>().ShouldBe("array");
    }

    [Fact]
    public void AnOwner_IsGivenToReadEditBuildTestCommitAndMerge_ButNotTheBoardOrTheTeamChannel()
    {
        var names = PipelineStageTools.OwnerToolNames;

        names.ShouldContain("edit_file");
        names.ShouldContain("build_solution");
        names.ShouldContain("run_tests");
        names.ShouldContain("run_command"); // offered only when the run allows commands - the dispatcher decides
        names.ShouldContain("team_commit");
        names.ShouldContain("team_sync");
        names.ShouldContain("team_merge");
        names.ShouldNotContain("team_post");
        names.ShouldNotContain("team_dm");
        names.ShouldNotContain("board_write");
        names.ShouldNotContain("board_read");
        names.ShouldNotContain("team_done");
    }

    private static string[] Required(JsonNode schema, string list) =>
        schema["properties"]![list]!["items"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
}
