using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TranscriptToolsTests
{
    [Fact]
    public void SearchTranscripts_IsOfferedToTheModel_AndRoutesAsBuiltIn()
    {
        BuiltInTools.All.ShouldContain(t => t.Name == TranscriptTools.SearchTranscriptsToolName);
        BuiltInTools.IsBuiltIn(TranscriptTools.SearchTranscriptsToolName).ShouldBeTrue();
        TranscriptTools.IsTranscriptTool(TranscriptTools.SearchTranscriptsToolName).ShouldBeTrue();
        TranscriptTools.IsTranscriptTool("read_skill").ShouldBeFalse();
    }

    [Fact]
    public void TheToolName_IsAcceptableToChatCompletionsApis()
    {
        // DeepSeek and OpenAI answer 400 to the whole request for a name outside this set.
        ToolNames.Sanitize(TranscriptTools.SearchTranscriptsToolName).ShouldBe(TranscriptTools.SearchTranscriptsToolName);
    }

    [Fact]
    public void TheSchema_RequiresAQuery_AndOffersTheKindsTheHandlerUnderstands()
    {
        var schema = TranscriptTools.SearchTranscripts.ParametersSchema;

        schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["query"]);
        schema["properties"]!.AsObject().Select(p => p.Key).ShouldBe(["query", "kind", "limit"], ignoreOrder: true);
        schema["properties"]!["kind"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>())
            .ShouldBe([TranscriptTools.KindAny, TranscriptTools.KindMessage, TranscriptTools.KindThought]);
    }

    [Fact]
    public void TheDescription_TellsTheModelWhenToUseItAndWhenNot()
    {
        var description = TranscriptTools.SearchTranscripts.Description;

        description.ShouldContain("earlier");
        description.ShouldContain("Do not use it for what is already in this conversation");
    }
}
