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

    private static string[] Required(JsonNode schema, string list) =>
        schema["properties"]![list]!["items"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
}
