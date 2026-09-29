using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

/// <summary>
/// Parsing only. Actually running gulp or grunt needs Node and a real project, which is an
/// integration concern - these pin the output shapes against captured samples so a tool-version
/// change that shifts them is a failing test rather than an empty task list.
/// </summary>
public class JavaScriptTaskParsingTests
{
    [Fact]
    public void Gulp_ReadsOneTaskPerLine() =>
        GulpTaskDiscovery.ParseTaskNames("build\nsass\nwatch\n").ShouldBe(["build", "sass", "watch"]);

    [Fact]
    public void Gulp_IgnoresBlankLines() =>
        GulpTaskDiscovery.ParseTaskNames("\n\nbuild\n\n  \nsass\n").ShouldBe(["build", "sass"]);

    /// <summary>Node writes warnings to the same stream; a line with spaces in it is prose, not a
    /// task name, and listing it would offer something unrunnable.</summary>
    [Fact]
    public void Gulp_DropsProseLines() =>
        GulpTaskDiscovery.ParseTaskNames(
            "(node:123) ExperimentalWarning: something is experimental\nbuild\nsass\n")
            .ShouldBe(["build", "sass"]);

    [Fact]
    public void Gulp_DeduplicatesRepeatedNames() =>
        GulpTaskDiscovery.ParseTaskNames("build\nbuild\nsass\n").ShouldBe(["build", "sass"]);

    [Fact]
    public void Gulp_NoOutput_YieldsNoTasks() =>
        GulpTaskDiscovery.ParseTaskNames("").ShouldBeEmpty();

    [Fact]
    public void Grunt_ReadsTheAvailableTasksSection()
    {
        const string Output = """
            Grunt: The JavaScript Task Runner

            Usage
              grunt [options] [task [task ...]]

            Options
                  --help, -h  Display this help text.

            Available tasks
                    concat  Concatenate files. *
                    uglify  Minify files with UglifyJS. *
                   default  Alias for "concat", "uglify" tasks.
            """;

        GruntTaskDiscovery.ParseTaskNames(Output).ShouldBe(["concat", "uglify", "default"]);
    }

    /// <summary>The options block above the task list must not be read as tasks - which is why the
    /// parser only starts at the heading.</summary>
    [Fact]
    public void Grunt_IgnoresEverythingBeforeTheHeading()
    {
        const string Output = """
            Options
                  --help, -h  Display this help text.
                     --base  Specify an alternate base path.

            Available tasks
                    concat  Concatenate files. *
            """;

        GruntTaskDiscovery.ParseTaskNames(Output).ShouldBe(["concat"]);
    }

    [Fact]
    public void Grunt_StopsAtTheEndOfTheSection()
    {
        const string Output = """
            Available tasks
                    concat  Concatenate files. *

            Tasks run in the order specified.
            """;

        GruntTaskDiscovery.ParseTaskNames(Output).ShouldBe(["concat"]);
    }

    [Fact]
    public void Grunt_NoAvailableTasksSection_YieldsNoTasks() =>
        GruntTaskDiscovery.ParseTaskNames("Grunt: The JavaScript Task Runner\n\nUsage\n  grunt [task]\n").ShouldBeEmpty();

    [Fact]
    public void Grunt_HandlesCarriageReturns() =>
        GruntTaskDiscovery.ParseTaskNames("Available tasks\r\n        concat  Concatenate files. *\r\n")
            .ShouldBe(["concat"]);
}
