using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class McpToolCallViewModelTests
{
    [Fact]
    public void A_new_call_is_running_with_arguments_visible()
    {
        var card = new McpToolCallViewModel("id-1", "rea__decompile", "{\"name\":\"main\"}");

        card.IsRunning.ShouldBeTrue();
        card.Status.ShouldBe("Running...");
        card.HasArguments.ShouldBeTrue();
    }

    [Fact]
    public void No_arguments_means_the_arguments_expander_is_hidden()
    {
        new McpToolCallViewModel("id", "tool", null).HasArguments.ShouldBeFalse();
        new McpToolCallViewModel("id", "tool", "   ").HasArguments.ShouldBeFalse();
    }

    [Fact]
    public void RefreshElapsed_updates_the_label_while_running()
    {
        var card = new McpToolCallViewModel("id", "tool", null);

        card.RefreshElapsed();

        card.ElapsedText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ReportProgress_with_a_message_shows_a_progress_line()
    {
        var card = new McpToolCallViewModel("id", "tool", null);

        card.ReportProgress("Decompiling", 3, 10);

        card.HasProgress.ShouldBeTrue();
        card.ProgressText.ShouldBe("Decompiling (3/10)");
    }

    [Fact]
    public void ReportProgress_with_only_counts_formats_a_ratio()
    {
        var card = new McpToolCallViewModel("id", "tool", null);

        card.ReportProgress(null, 7, 20);

        card.ProgressText.ShouldBe("7/20");
    }

    [Fact]
    public void Complete_stops_running_and_keeps_the_full_result()
    {
        var card = new McpToolCallViewModel("id", "tool", null);
        card.ReportProgress("working", 1, 2);
        var longResult = new string('x', 500);

        card.Complete(isError: false, longResult);

        card.IsRunning.ShouldBeFalse();
        card.Status.ShouldBe("Done");
        card.ProgressText.ShouldBeNull();
        card.ResultText.ShouldBe(longResult);
        card.ResultPreview!.Length.ShouldBe(401);
        card.ResultPreview.ShouldEndWith("…");
        card.ElapsedText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Complete_with_an_error_sets_the_error_state()
    {
        var card = new McpToolCallViewModel("id", "tool", null);

        card.Complete(isError: true, "boom");

        card.IsError.ShouldBeTrue();
        card.Status.ShouldBe("Error");
    }

    [Fact]
    public void Progress_after_completion_is_ignored()
    {
        var card = new McpToolCallViewModel("id", "tool", null);
        card.Complete(isError: false, "done");

        card.ReportProgress("late", 1, 1);

        card.ProgressText.ShouldBeNull();
    }
}
