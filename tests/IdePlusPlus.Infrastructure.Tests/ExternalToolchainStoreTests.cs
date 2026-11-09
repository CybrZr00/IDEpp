using System.IO.Compression;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

public sealed class ExternalToolchainStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusToolchainTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ExternalTool TestTool(string probe = "support/analyzeHeadless.bat") =>
        new("demo", "Demo", "A test tool.", "1.0", "https://example/none.zip", "00", 1, probe);

    private ExternalToolchainStore NewStore(Func<string, string?>? detect = null) =>
        new(Path.Combine(_root, "tools"), [TestTool()], detect ?? (_ => null));

    [Fact]
    public void Default_tools_include_pinned_ghidra_and_jdk()
    {
        var store = new ExternalToolchainStore(Path.Combine(_root, "tools"));

        var ghidra = store.Find(ExternalToolchainStore.GhidraId).ShouldNotBeNull();
        ghidra.Sha256.Length.ShouldBe(64);
        ghidra.DownloadUrl.ShouldStartWith("https://");
        store.Find(ExternalToolchainStore.Jdk21Id).ShouldNotBeNull().Sha256.Length.ShouldBe(64);
    }

    [Fact]
    public void State_is_not_installed_when_nothing_is_present_or_detected()
    {
        var store = NewStore();

        store.State("demo").ShouldBe(ToolchainState.NotInstalled);
        store.ResolveToolRoot("demo").ShouldBeNull();
    }

    [Fact]
    public void State_is_detected_when_an_external_copy_exists()
    {
        var external = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(external);
        var store = NewStore(id => id == "demo" ? external : null);

        store.State("demo").ShouldBe(ToolchainState.DetectedExternally);
        store.ResolveToolRoot("demo").ShouldBe(external);
    }

    [Fact]
    public void Installing_from_an_archive_marks_it_installed_and_resolves_the_root()
    {
        var store = NewStore();
        var archive = BuildArchive(withTopFolder: false);

        store.InstallFromArchive("demo", archive);

        store.IsInstalled("demo").ShouldBeTrue();
        store.State("demo").ShouldBe(ToolchainState.Installed);
        store.ResolveToolRoot("demo").ShouldBe(store.InstallDirectory("demo"));
    }

    [Fact]
    public void Resolve_finds_the_root_inside_a_single_top_level_folder()
    {
        var store = NewStore();
        var archive = BuildArchive(withTopFolder: true);

        store.InstallFromArchive("demo", archive);

        store.ResolveToolRoot("demo").ShouldBe(Path.Combine(store.InstallDirectory("demo"), "inner"));
    }

    [Fact]
    public void Detected_external_wins_over_an_installed_copy()
    {
        var external = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(external);
        var store = NewStore(id => id == "demo" ? external : null);
        store.InstallFromArchive("demo", BuildArchive(withTopFolder: false));

        store.ResolveToolRoot("demo").ShouldBe(external);
    }

    private string BuildArchive(bool withTopFolder)
    {
        var contentDir = Path.Combine(_root, "content", Guid.NewGuid().ToString("N"));
        var probeDir = withTopFolder
            ? Path.Combine(contentDir, "inner", "support")
            : Path.Combine(contentDir, "support");
        Directory.CreateDirectory(probeDir);
        File.WriteAllText(Path.Combine(probeDir, "analyzeHeadless.bat"), "echo hi");

        var zipPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(contentDir, zipPath);
        return zipPath;
    }
}
