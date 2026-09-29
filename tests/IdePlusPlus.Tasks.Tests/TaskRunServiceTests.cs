using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

/// <summary>
/// Covers the refusals only - everything a task run does after them needs a real package manager
/// installed, which is an integration concern rather than something to assert here. The refusals
/// are checked before the tool is even located, so these stay deterministic on a machine with or
/// without Node.
/// </summary>
public class TaskRunServiceTests
{
    /// <summary>Task names come out of a file in the repository. The package manager resolves to a
    /// .cmd, which CreateProcess runs via cmd.exe, so an argument containing shell metacharacters
    /// is parsed by a parser ArgumentList does not escape for - hence refuse rather than escape.</summary>
    [Theory]
    [InlineData("build & calc")]
    [InlineData("build\"quote")]
    [InlineData("build%PATH%")]
    [InlineData("build|more")]
    [InlineData("-build")]
    [InlineData("")]
    public async Task RunAsync_UnsafeTaskName_IsRefusedWithoutStartingAnything(string taskName)
    {
        var service = new TaskRunService();
        var output = new List<string>();

        var exitCode = await service.RunAsync(
            new TaskDefinition(TaskManifestKind.NpmScripts, @"C:\repo\package.json", taskName, "whatever"),
            NodePackageManager.Npm,
            output.Add);

        exitCode.ShouldBe(-1);
        output.ShouldContain(line => line.Contains("Refusing to run"));
        service.IsRunning.ShouldBeFalse();
    }

    [Theory]
    [InlineData("build")]
    [InlineData("build:prod")]
    [InlineData("test-watch")]
    [InlineData("lint.fix")]
    [InlineData("build_all")]
    [InlineData("a11y")]
    public async Task RunAsync_OrdinaryScriptNames_GetPastTheNameCheck(string taskName)
    {
        var service = new TaskRunService();
        var output = new List<string>();

        // A manifest path with no directory is the next refusal after the name check, so reaching
        // its message proves the name itself was accepted - without needing npm on the machine.
        var exitCode = await service.RunAsync(
            new TaskDefinition(TaskManifestKind.NpmScripts, "package.json", taskName, "whatever"),
            NodePackageManager.Npm,
            output.Add);

        exitCode.ShouldBe(-1);
        output.ShouldContain(line => line.Contains("has no containing directory"));
    }
}
