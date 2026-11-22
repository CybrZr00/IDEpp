using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>What the pipeline's orchestrator is offered, and how what it sends back is read.</summary>
public sealed class PipelineLeadToolsTests
{
    private static readonly string[] SubmitTools =
    [
        PipelineLeadTools.SubmitRosterToolName,
        PipelineLeadTools.SubmitTriageToolName,
        PipelineLeadTools.ArbitrateToolName,
    ];

    [Fact]
    public void TheThreeSubmitTools_HaveTheirOwnNames_NotTheOriginalLeadsOwn()
    {
        SubmitTools.Distinct().Count().ShouldBe(3);
        SubmitTools.ShouldAllBe(name => name.StartsWith("lead_"));
        SubmitTools.ShouldNotContain(LeadTools.SubmitPlanToolName);
        SubmitTools.ShouldNotContain(LeadTools.TriageToolName);
        SubmitTools.ShouldNotContain(LeadTools.VerdictToolName);
    }

    [Theory]
    [InlineData(PipelineLeadTools.SubmitRosterToolName)]
    [InlineData(PipelineLeadTools.SubmitTriageToolName)]
    [InlineData(PipelineLeadTools.ArbitrateToolName)]
    public void EachDecision_IsMadeWithTheReadingToolsPlusItsOwnSubmitTool_AndNothingThatWrites(string submit)
    {
        var tools = submit switch
        {
            PipelineLeadTools.SubmitRosterToolName => PipelineLeadTools.ForRoster,
            PipelineLeadTools.SubmitTriageToolName => PipelineLeadTools.ForTriage,
            _ => PipelineLeadTools.ForArbitration,
        };

        tools.Select(t => t.Name).ShouldBe([.. SubAgentTools.AllowedTools.Select(t => t.Name), submit]);
        tools.Select(t => t.Name).ShouldNotContain("edit_file");
        tools.Select(t => t.Name).ShouldNotContain("run_command");
    }

    [Fact]
    public void EverySchema_IsAnObject_AndTheSubmitToolsRequireWhatTheirValidatorsDo()
    {
        foreach (var tool in new[] { PipelineLeadTools.SubmitRoster, PipelineLeadTools.SubmitTriage, PipelineLeadTools.Arbitrate })
        {
            tool.ParametersSchema["type"]!.GetValue<string>().ShouldBe("object");
            tool.Description.ShouldNotBeNullOrWhiteSpace();
        }

        Required(PipelineLeadTools.SubmitTriage).ShouldBe(["decisions"]);
        Required(PipelineLeadTools.Arbitrate).ShouldBe(["request", "ruling", "instruction"]);

        // An empty roster is a decision, so nothing in a roster is required.
        Required(PipelineLeadTools.SubmitRoster).ShouldBeEmpty();
    }

    [Fact]
    public void TheRosterSchema_OffersOnlyTheTwoScopesAJudgeCanHave()
    {
        var scope = PipelineLeadTools.SubmitRoster.ParametersSchema["properties"]!["customJudges"]!["items"]!["properties"]!["scope"]!;

        scope["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["none", "testsOnly"]);
    }

    [Fact]
    public void ARosterSubmission_IsReadFromItsJson_AndChecksOutAgainstTheLimits()
    {
        var arguments = JsonNode.Parse("""
            {
              "surveyors": [ { "area": "Lib/Parsing", "question": "How are errors reported?" }, { "area": "Lib/Output" } ],
              "customJudges": [ { "title": "Accessibility judge", "charter": "Checks every control has a name.", "scope": "testsOnly", "focusFiles": ["Lib/Ui.cs"] } ],
              "rationale": "Two areas, one risk."
            }
            """);

        var parsed = PipelineToolArguments.Parse<RosterDecision>(arguments, PipelineLeadTools.SubmitRosterToolName);
        parsed.IsValid.ShouldBeTrue(parsed.Feedback);
        var plan = RosterValidator.Validate(parsed.Value, new RosterLimits(3, 2)).Value!;

        plan.Surveyors.Select(s => (s.Agent.Name, s.Focus.Area)).ShouldBe([("u1", "Lib/Parsing"), ("u2", "Lib/Output")]);
        plan.Surveyors[0].Focus.Question.ShouldBe("How are errors reported?");
        plan.CustomJudges.ShouldHaveSingleItem().Agent.Name.ShouldBe("j5");
        plan.CustomJudges[0].FocusFiles.ShouldBe(["Lib/Ui.cs"]);
        plan.Rationale.ShouldBe("Two areas, one risk.");
    }

    [Fact]
    public void ATriageSubmission_IsReadFromItsJson_InCamelCase()
    {
        var arguments = JsonNode.Parse("""
            { "decisions": [ { "finding": "F1.1", "action": "defer", "reason": "Outside the goal, and a bigger change.", "nextPrompt": "Cache the loader." } ], "notes": "n" }
            """);

        var parsed = PipelineToolArguments.Parse<TriageSubmission>(arguments, PipelineLeadTools.SubmitTriageToolName);

        parsed.IsValid.ShouldBeTrue(parsed.Feedback);
        parsed.Value!.Decisions.ShouldHaveSingleItem().NextPrompt.ShouldBe("Cache the loader.");
        parsed.Value.Notes.ShouldBe("n");
    }

    [Fact]
    public void PropertyNamesInAnyCase_AreAccepted_AndAMissingListIsJustEmpty()
    {
        var parsed = PipelineToolArguments.Parse<RosterDecision>(
            JsonNode.Parse("""{ "SURVEYORS": [ { "Area": "Lib" } ] }"""), PipelineLeadTools.SubmitRosterToolName);

        parsed.IsValid.ShouldBeTrue(parsed.Feedback);
        parsed.Value!.Surveyors.ShouldHaveSingleItem().Area.ShouldBe("Lib");
        RosterValidator.Validate(parsed.Value, new RosterLimits(3, 2)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ArgumentsThatCannotBeRead_AreSaidSoByToolName_WithoutThrowing()
    {
        var wrongShape = PipelineToolArguments.Parse<TriageSubmission>(JsonNode.Parse("[1, 2]"), PipelineLeadTools.SubmitTriageToolName);
        var wrongType = PipelineToolArguments.Parse<TriageSubmission>(
            JsonNode.Parse("""{ "decisions": "all of them" }"""), PipelineLeadTools.SubmitTriageToolName);
        var none = PipelineToolArguments.Parse<ArbitrationSubmission>(null, PipelineLeadTools.ArbitrateToolName);

        wrongShape.IsValid.ShouldBeFalse();
        wrongShape.Feedback.ShouldContain($"{PipelineLeadTools.SubmitTriageToolName}'s arguments could not be read");
        wrongType.IsValid.ShouldBeFalse();
        none.IsValid.ShouldBeFalse();
        none.Feedback.ShouldContain($"{PipelineLeadTools.ArbitrateToolName} needs its arguments");
    }

    private static string[] Required(ToolDefinition tool) =>
        tool.ParametersSchema["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
}
