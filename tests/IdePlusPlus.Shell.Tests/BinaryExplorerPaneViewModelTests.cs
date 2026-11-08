using IdePlusPlus.ReverseEngineering;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class BinaryExplorerPaneViewModelTests
{
    private static string ManagedAssembly => typeof(ManagedDecompiler).Assembly.Location;

    [Fact]
    public async Task Loading_a_managed_assembly_lists_its_types()
    {
        var pane = new BinaryExplorerPaneViewModel { BinaryPath = ManagedAssembly };

        await pane.LoadCommand.ExecuteAsync(null);

        pane.ItemsHeader.ShouldBe("Types");
        pane.Items.ShouldContain("IdePlusPlus.ReverseEngineering.ManagedDecompiler");
        pane.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Loading_a_missing_file_reports_it()
    {
        var pane = new BinaryExplorerPaneViewModel { BinaryPath = @"Z:\no\such\file.dll" };

        await pane.LoadCommand.ExecuteAsync(null);

        pane.StatusText.ShouldContain("No file found");
        pane.Items.ShouldBeEmpty();
    }
}
