using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

public sealed class BreakpointManagerTests : IDisposable
{
    private readonly string _breakpointsPath = Path.Combine(Path.GetTempPath(), $"ideplusplus-bp-{Guid.NewGuid()}.json");

    public void Dispose() => File.Delete(_breakpointsPath);

    [Fact]
    public void Toggle_OnAndOff_TogglesIsBreakpoint()
    {
        var manager = new BreakpointManager(_breakpointsPath);

        manager.Toggle("Foo.cs", 10);
        manager.IsBreakpoint("Foo.cs", 10).ShouldBeTrue();

        manager.Toggle("Foo.cs", 10);
        manager.IsBreakpoint("Foo.cs", 10).ShouldBeFalse();
    }

    [Fact]
    public void IsBreakpoint_FilePathComparisonIsCaseInsensitive()
    {
        var manager = new BreakpointManager(_breakpointsPath);

        manager.Toggle(@"C:\Repo\Foo.cs", 5);

        manager.IsBreakpoint(@"c:\repo\foo.cs", 5).ShouldBeTrue();
    }

    [Fact]
    public void GetLines_ReturnsAllLinesForAFile()
    {
        var manager = new BreakpointManager(_breakpointsPath);

        manager.Toggle("Foo.cs", 1);
        manager.Toggle("Foo.cs", 5);

        manager.GetLines("Foo.cs").ShouldBe([1, 5], ignoreOrder: true);
    }

    [Fact]
    public void Changed_RaisedOnToggle()
    {
        var manager = new BreakpointManager(_breakpointsPath);
        var raised = false;
        manager.Changed += () => raised = true;

        manager.Toggle("Foo.cs", 1);

        raised.ShouldBeTrue();
    }

    [Fact]
    public void Toggle_PersistsAcrossInstances()
    {
        new BreakpointManager(_breakpointsPath).Toggle("Foo.cs", 42);

        var reloaded = new BreakpointManager(_breakpointsPath);

        reloaded.IsBreakpoint("Foo.cs", 42).ShouldBeTrue();
    }

    [Fact]
    public void CorruptBreakpointsFile_LoadsAsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_breakpointsPath)!);
        File.WriteAllText(_breakpointsPath, "{ not valid json");

        var manager = new BreakpointManager(_breakpointsPath);

        manager.IsBreakpoint("Foo.cs", 1).ShouldBeFalse();
    }

    [Fact]
    public void MissingBreakpointsFile_LoadsAsEmpty()
    {
        var manager = new BreakpointManager(_breakpointsPath);

        manager.AllBreakpoints.ShouldBeEmpty();
    }
}
