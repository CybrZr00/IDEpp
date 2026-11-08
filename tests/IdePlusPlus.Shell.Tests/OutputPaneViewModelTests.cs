using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class OutputPaneViewModelTests
{
    [Fact]
    public void Starts_with_only_the_general_channel()
    {
        var pane = new OutputPaneViewModel();

        pane.Channels.ShouldBe([OutputPaneViewModel.DefaultChannelName]);
        pane.SelectedChannel.ShouldBe(OutputPaneViewModel.DefaultChannelName);
    }

    [Fact]
    public void Parameterless_append_writes_to_the_selected_general_channel()
    {
        var pane = new OutputPaneViewModel();

        pane.AppendLine("hello world");

        pane.Text.ShouldContain("hello world");
    }

    [Fact]
    public void Writing_to_another_channel_adds_it_but_does_not_change_the_current_view()
    {
        var pane = new OutputPaneViewModel();
        pane.AppendLine("general line");

        pane.AppendLine(OutputPaneViewModel.BuildChannel, "build line");

        pane.Channels.ShouldContain(OutputPaneViewModel.BuildChannel);
        pane.SelectedChannel.ShouldBe(OutputPaneViewModel.DefaultChannelName);
        pane.Text.ShouldNotContain("build line");
        pane.Text.ShouldContain("general line");
    }

    [Fact]
    public void Selecting_a_channel_shows_that_channels_content()
    {
        var pane = new OutputPaneViewModel();
        pane.AppendLine(OutputPaneViewModel.BuildChannel, "build line");

        pane.SelectedChannel = OutputPaneViewModel.BuildChannel;

        pane.Text.ShouldContain("build line");
    }

    [Fact]
    public void Clear_channel_brings_it_to_the_front_and_empties_it()
    {
        var pane = new OutputPaneViewModel();
        pane.AppendLine(OutputPaneViewModel.BuildChannel, "stale line");

        pane.Clear(OutputPaneViewModel.BuildChannel);

        pane.SelectedChannel.ShouldBe(OutputPaneViewModel.BuildChannel);
        pane.Text.ShouldNotContain("stale line");

        pane.AppendLine(OutputPaneViewModel.BuildChannel, "fresh line");
        pane.Text.ShouldContain("fresh line");
    }

    [Fact]
    public void Channels_are_bounded_dropping_the_oldest_lines()
    {
        var pane = new OutputPaneViewModel();

        for (var i = 1; i <= 6_000; i++)
        {
            pane.AppendLine($"line {i}");
        }

        var lineCount = pane.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        lineCount.ShouldBeLessThanOrEqualTo(5_000);
        pane.Text.ShouldContain("line 6000");
        pane.Text.ShouldNotContain("line 1\n");
    }

    [Fact]
    public void Post_is_used_to_marshal_ui_updates()
    {
        var pane = new OutputPaneViewModel();
        var posted = 0;
        pane.Post = action =>
        {
            posted++;
            action();
        };

        pane.AppendLine(OutputPaneViewModel.McpChannel, "line");

        posted.ShouldBeGreaterThan(0);
        pane.Channels.ShouldContain(OutputPaneViewModel.McpChannel);
    }
}
