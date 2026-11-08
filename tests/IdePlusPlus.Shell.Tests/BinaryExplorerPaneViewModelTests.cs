using IdePlusPlus.ReverseEngineering;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class BinaryExplorerPaneViewModelTests
{
    private static string ManagedAssembly => typeof(ManagedDecompiler).Assembly.Location;

    [Fact]
    public void The_pane_hosts_a_shared_viewer()
    {
        new BinaryExplorerPaneViewModel().Viewer.ShouldNotBeNull();
    }

    [Fact]
    public async Task Loading_a_managed_assembly_lists_its_types()
    {
        var viewer = new BinaryViewerViewModel { BinaryPath = ManagedAssembly };

        await viewer.LoadCommand.ExecuteAsync(null);

        viewer.ItemsHeader.ShouldBe("Types");
        viewer.Items.ShouldContain("IdePlusPlus.ReverseEngineering.ManagedDecompiler");
        viewer.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task Loading_a_missing_file_reports_it()
    {
        var viewer = new BinaryViewerViewModel { BinaryPath = @"Z:\no\such\file.dll" };

        await viewer.LoadCommand.ExecuteAsync(null);

        viewer.StatusText.ShouldContain("No file found");
        viewer.Items.ShouldBeEmpty();
    }

    [Fact]
    public void A_binary_document_opens_the_viewer_on_its_path()
    {
        var document = new BinaryDocumentViewModel(ManagedAssembly);

        document.FilePath.ShouldBe(ManagedAssembly);
        document.Viewer.BinaryPath.ShouldBe(ManagedAssembly);
        document.IsDirty.ShouldBeFalse();
    }
}
