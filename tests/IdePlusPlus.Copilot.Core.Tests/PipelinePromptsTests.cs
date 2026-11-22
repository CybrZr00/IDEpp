using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public sealed class PipelinePromptsTests
{
    [Fact]
    public void AReminder_NamesTheToolToCall_AndHowManyReminders()
    {
        var text = PipelinePrompts.MissingSubmission("survey_submit", 1, 2);

        text.ShouldContain("survey_submit");
        text.ShouldContain("reminder 1 of 2");
        text.ShouldContain("Nothing you wrote in the conversation is recorded");
    }

    [Fact]
    public void EveryWrapUp_NamesTheTool_AndSaysItsOwnReason()
    {
        var messages = Enum.GetValues<WrapUpReason>()
            .Select(reason => PipelinePrompts.WrapUp("verdict_submit", reason))
            .ToList();

        messages.ShouldAllBe(m => m.Contains("verdict_submit") && m.Contains("what you did not get to"));

        // The reason is the first sentence, and it differs for each: an agent told "out of time" when it
        // was out of tokens would be told something untrue.
        messages.Select(m => m.Split('.')[0]).Distinct().Count().ShouldBe(messages.Count);
        PipelinePrompts.WrapUp("x", WrapUpReason.Time).ShouldStartWith("Your stage has run out of time.");
        PipelinePrompts.WrapUp("x", WrapUpReason.Tokens).ShouldContain("token");
        PipelinePrompts.WrapUp("x", WrapUpReason.Context).ShouldContain("too large");
        PipelinePrompts.WrapUp("x", WrapUpReason.Steps).ShouldContain("every step");
    }

    [Fact]
    public void ARefusedSubmission_SaysWhy_AndToCallTheToolAgain()
    {
        var text = PipelinePrompts.NotRecorded("survey_submit", "- exists[1].file: '' is not a plain relative path.");

        text.ShouldStartWith("Not recorded:\n- exists[1].file");
        text.ShouldEndWith("Fix these and call survey_submit again.");
    }

    [Fact]
    public void ASurveyorsPrompt_OpensAsItsName_CarriesItsRolesCharter_AndNamesTheOneToolThatCounts()
    {
        var prompt = PipelinePrompts.Surveyor("u2", PipelineRoles.Understander);

        prompt.ShouldStartWith("You are u2, a surveyor");
        prompt.ShouldContain(PipelineRoles.Understander.Charter);
        prompt.ShouldContain("survey_submit");
        prompt.ShouldContain("only that submission");
        prompt.ShouldContain("the user is not available");
        prompt.ShouldContain("read-only");
    }

    [Fact]
    public void ASurveyorsOpening_GivesTheGoalAndTheAreaAndTheQuestionIfThereIsOne()
    {
        var with = PipelinePrompts.SurveyorOpening("  Add a --json flag.  ", new SurveyFocus("Lib/Cli", "How are flags parsed?"));
        var without = PipelinePrompts.SurveyorOpening("Add a --json flag.", new SurveyFocus("Lib/Cli"));

        with.ShouldStartWith("The goal of the whole run:\nAdd a --json flag.\n\nYour area: Lib/Cli");
        with.ShouldContain("The question that matters most there: How are flags parsed?");
        with.ShouldEndWith("Survey your area, then call survey_submit.");
        without.ShouldNotContain("The question that matters");
    }
}
