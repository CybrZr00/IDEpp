using IdePlusPlus.Copilot.Core;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class PlanProposalParserTests
{
    /// <summary>Feeds the arguments in small fragments, the way they stream, and returns the last event.</summary>
    private static PlanProposalEvent Stream(PlanProposalParser parser, string arguments, int fragmentLength = 7)
    {
        var lastEvent = parser.OnToolCallDelta(new ToolCallDelta(0, "plan_1", "propose_plan", ""));
        for (var i = 0; i < arguments.Length; i += fragmentLength)
        {
            var fragment = arguments.Substring(i, Math.Min(fragmentLength, arguments.Length - i));
            lastEvent = parser.OnToolCallDelta(new ToolCallDelta(0, null, null, fragment));
        }

        return lastEvent;
    }

    private static PlanOperation Parse(string arguments)
    {
        var lastEvent = Stream(new PlanProposalParser(), arguments);
        lastEvent.Status.ShouldBe(PlanProposalStatus.Complete);
        return lastEvent.Plan!;
    }

    [Fact]
    public void StringSteps_AreTakenAsTheyAre()
    {
        var plan = Parse("""{"summary": "Do it", "steps": ["Read the file", "Change it"]}""");

        plan.Summary.ShouldBe("Do it");
        plan.Steps.ShouldBe(["Read the file", "Change it"]);
    }

    [Fact]
    public void ObjectSteps_AreReadAsHeadingAndDetail()
    {
        var plan = Parse("""
            {"summary": "S", "steps": [
              {"title": "Front-load the true state", "detail": "port complete, parity unproven"},
              {"title": "Add a protocol"},
              {"step": 3, "description": "Replace the stale parts"}
            ]}
            """);

        plan.Steps.ShouldBe([
            "Front-load the true state - port complete, parity unproven",
            "Add a protocol",
            "Replace the stale parts",
        ]);
    }

    [Fact]
    public void ObjectSteps_WithUnknownKeys_KeepAllTheirText()
    {
        var plan = Parse("""{"summary": "S", "steps": [{"what": "Rewrite section 7", "why": "it is dormant"}, {"n": 2}]}""");

        plan.Steps.ShouldBe(["Rewrite section 7 - it is dormant", "2"]);
    }

    [Fact]
    public void NestedListsAndNumbers_AreFlattenedToText()
    {
        var plan = Parse("""{"summary": "S", "steps": [["Read", "Edit"], 42, null, ""]}""");

        plan.Steps.ShouldBe(["Read; Edit", "42"]);
    }

    [Fact]
    public void StepsSentAsOneString_AreSplitIntoLines()
    {
        var plan = Parse("""{"summary": "S", "steps": "1. Read\n\n2. Edit\n"}""");

        plan.Steps.ShouldBe(["1. Read", "2. Edit"]);
    }

    /// <summary>What a failed Ultra run's board showed: the first TASK was literally
    /// <c>&lt;parameter name="steps"&gt;[</c> and the rest were quoted, with a comma on the end, because
    /// the model sent its JSON list wrapped in the markup of a tool call, inside a string.</summary>
    [Fact]
    public void StepsSentAsAJsonListInsideAString_WrappedInParameterMarkup_AreReadAsTheList()
    {
        var plan = Parse(
            "{\"summary\": \"S\", \"steps\": \"<parameter name=\\\"steps\\\">[\\n\\\"CONTRACT: add the interface\\\",\\n"
            + "\\\"HOST: \\\\\\\"quote\\\\\\\" it\\\",\\n\\\"TEST: cover it\\\"\\n]</parameter>\"}");

        plan.Steps.ShouldBe(["CONTRACT: add the interface", "HOST: \"quote\" it", "TEST: cover it"]);
    }

    [Fact]
    public void StepsSentAsABareJsonListInsideAString_AreReadAsTheList()
    {
        var plan = Parse("""{"summary": "S", "steps": "[\"Read\", \"Edit\"]"}""");

        plan.Steps.ShouldBe(["Read", "Edit"]);
    }

    [Fact]
    public void StepsSentAsALineListThatHappensToStartWithABracket_StaysALineList()
    {
        var plan = Parse("""{"summary": "S", "steps": "[x] Read\n[ ] Edit"}""");

        plan.Steps.ShouldBe(["[x] Read", "[ ] Edit"]);
    }

    [Fact]
    public void ArgumentsSentAsAJsonString_AreUnwrapped()
    {
        var plan = Parse("\"{\\\"summary\\\": \\\"S\\\", \\\"steps\\\": [\\\"One\\\"]}\"");

        plan.Summary.ShouldBe("S");
        plan.Steps.ShouldBe(["One"]);
    }

    [Fact]
    public void MidStream_ItIsStreamingWithASummaryPreview()
    {
        var lastEvent = Stream(new PlanProposalParser(), """{"summary": "Rewrite the doc", "steps": ["Rea""");

        lastEvent.Status.ShouldBe(PlanProposalStatus.Streaming);
        lastEvent.SummaryPreview.ShouldBe("Rewrite the doc");
    }

    [Fact]
    public void Finish_ReportsAPlanThatWasCutOff_WithWhatWasSent()
    {
        const string arguments = """{"summary": "Rewrite the doc", "steps": ["Read it", "Rew""";
        var parser = new PlanProposalParser();
        Stream(parser, arguments);

        var failure = parser.Finish().ShouldHaveSingleItem();

        failure.ToolCallId.ShouldBe("plan_1");
        failure.Reason.ShouldContain("isn't valid JSON");
        failure.RawArguments.ShouldBe(arguments);
    }

    [Theory]
    [InlineData("""{"summary": "S"}""", "no \"steps\" list")]
    [InlineData("""{"steps": ["One"]}""", "no \"summary\" text")]
    [InlineData("""{"summary": 5, "steps": ["One"]}""", "no \"summary\" text")]
    [InlineData("""["One"]""", "isn't a JSON object")]
    [InlineData("", "no plan at all")]
    public void Finish_SaysWhatIsWrong(string arguments, string reason)
    {
        var parser = new PlanProposalParser();
        Stream(parser, arguments);

        parser.Finish().ShouldHaveSingleItem().Reason.ShouldContain(reason);
    }

    [Fact]
    public void Finish_ReportsNothingForAPlanThatParsed()
    {
        var parser = new PlanProposalParser();
        Stream(parser, """{"summary": "S", "steps": ["One"]}""");

        parser.Finish().ShouldBeEmpty();
    }
}
