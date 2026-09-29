using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class WatchTaskDetectorTests
{
    private static TaskDefinition Task(string name, string? command = null) =>
        new(TaskManifestKind.NpmScripts, @"C:\repo\package.json", name, command);

    [Theory]
    [InlineData("watch")]
    [InlineData("test:watch")]
    [InlineData("Watch")]
    [InlineData("serve")]
    public void LooksLikeAWatchTask_ByName(string name) =>
        WatchTaskDetector.LooksLikeAWatchTask(Task(name)).ShouldBeTrue();

    [Theory]
    [InlineData("tsc --watch")]
    [InlineData("jest -w")]
    [InlineData("nodemon server.js")]
    [InlineData("webpack-dev-server")]
    [InlineData("vite")]
    public void LooksLikeAWatchTask_ByCommand(string command) =>
        WatchTaskDetector.LooksLikeAWatchTask(Task("start", command)).ShouldBeTrue();

    [Theory]
    [InlineData("build", "tsc")]
    [InlineData("test", "jest --ci")]
    [InlineData("lint", "eslint .")]
    [InlineData("bundle", "vite build")]
    [InlineData("clean", "rimraf dist")]
    public void OrdinaryTasks_AreNotFlagged(string name, string command) =>
        WatchTaskDetector.LooksLikeAWatchTask(Task(name, command)).ShouldBeFalse();

    [Fact]
    public void NoCommand_IsJudgedOnTheNameAlone()
    {
        WatchTaskDetector.LooksLikeAWatchTask(Task("build")).ShouldBeFalse();
        WatchTaskDetector.LooksLikeAWatchTask(Task("watch")).ShouldBeTrue();
    }
}
