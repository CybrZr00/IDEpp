using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class TaskBindingStoreTests
{
    [Fact]
    public void Load_NoFile_ReturnsEmpty()
    {
        using var directory = new TempDirectory();

        TaskBindingStore.Load(directory.Path).ShouldBeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var directory = new TempDirectory();
        TaskBinding[] bindings =
        [
            new("package.json", "build", TaskBindingKind.BeforeBuild),
            new("src/Web/package.json", "bundle", TaskBindingKind.AfterBuild),
            new("package.json", "clean", TaskBindingKind.BeforeClean),
        ];

        TaskBindingStore.Save(directory.Path, bindings);

        TaskBindingStore.Load(directory.Path).ShouldBe(bindings);
    }

    [Fact]
    public void Save_WritesUnderTheSolutionsIdePlusPlusDirectory()
    {
        using var directory = new TempDirectory();

        TaskBindingStore.Save(directory.Path, [new TaskBinding("package.json", "build", TaskBindingKind.BeforeBuild)]);

        File.Exists(Path.Combine(directory.Path, ".ideplusplus", "tasks.json")).ShouldBeTrue();
    }

    /// <summary>Kinds are stored by name, so the file stays readable and a reordered enum cannot
    /// silently repoint every binding.</summary>
    [Fact]
    public void Save_WritesKindsAsNames()
    {
        using var directory = new TempDirectory();

        TaskBindingStore.Save(directory.Path, [new TaskBinding("package.json", "build", TaskBindingKind.BeforeBuild)]);

        var json = File.ReadAllText(Path.Combine(directory.Path, ".ideplusplus", "tasks.json"));
        json.ShouldContain("BeforeBuild");
        json.ShouldContain("package.json");
    }

    [Fact]
    public void Load_MalformedFile_ReturnsEmptyRatherThanThrowing()
    {
        using var directory = new TempDirectory();
        directory.WriteFile(".ideplusplus/tasks.json", "{ not json");

        TaskBindingStore.Load(directory.Path).ShouldBeEmpty();
    }

    [Fact]
    public void Load_SkipsEntriesMissingAManifestOrTask()
    {
        using var directory = new TempDirectory();
        directory.WriteFile(".ideplusplus/tasks.json", """
            {
              "bindings": [
                { "manifest": "package.json", "task": "build", "kind": "BeforeBuild" },
                { "manifest": "", "task": "orphan", "kind": "BeforeBuild" },
                { "manifest": "package.json", "kind": "AfterBuild" }
              ]
            }
            """);

        TaskBindingStore.Load(directory.Path).ShouldBe([new TaskBinding("package.json", "build", TaskBindingKind.BeforeBuild)]);
    }

    [Fact]
    public void Save_OverwritesRatherThanAppends()
    {
        using var directory = new TempDirectory();

        TaskBindingStore.Save(directory.Path, [new TaskBinding("package.json", "build", TaskBindingKind.BeforeBuild)]);
        TaskBindingStore.Save(directory.Path, [new TaskBinding("package.json", "test", TaskBindingKind.AfterBuild)]);

        TaskBindingStore.Load(directory.Path).ShouldBe([new TaskBinding("package.json", "test", TaskBindingKind.AfterBuild)]);
    }

    [Fact]
    public void ToRelativePath_IsForwardSlashedAndSolutionRelative()
    {
        var relative = TaskBindingStore.ToRelativePath(@"C:\repo", @"C:\repo\src\Web\package.json");

        relative.ShouldBe("src/Web/package.json");
    }

    [Fact]
    public void ToRelativePath_ManifestBesideTheSolution_IsJustTheFileName() =>
        TaskBindingStore.ToRelativePath(@"C:\repo", @"C:\repo\package.json").ShouldBe("package.json");

    [Fact]
    public void ToAbsolutePath_ReversesToRelativePath()
    {
        var absolute = TaskBindingStore.ToAbsolutePath(@"C:\repo", "src/Web/package.json");

        absolute.ShouldBe(@"C:\repo\src\Web\package.json");
    }

    /// <summary>A manifest outside the solution directory cannot be made relative usefully, so it is
    /// stored absolute - which still has to round-trip.</summary>
    [Fact]
    public void ToAbsolutePath_RoundTripsAnAbsoluteStoredPath()
    {
        var stored = TaskBindingStore.ToRelativePath(@"C:\repo", @"D:\elsewhere\package.json");

        TaskBindingStore.ToAbsolutePath(@"C:\repo", stored).ShouldBe(@"D:\elsewhere\package.json");
    }
}
