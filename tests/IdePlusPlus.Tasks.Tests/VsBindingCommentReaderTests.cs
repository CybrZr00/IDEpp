using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class VsBindingCommentReaderTests
{
    [Fact]
    public void Parse_NoComment_ReturnsNull() =>
        VsBindingCommentReader.Parse("var gulp = require('gulp');").ShouldBeNull();

    [Fact]
    public void Parse_ReadsTheCommonSingleQuotedForm()
    {
        var comment = VsBindingCommentReader.Parse("/// <binding BeforeBuild='sass' />");

        comment.ShouldNotBeNull();
        comment.Bindings.ShouldBe([new VsBinding("sass", TaskBindingKind.BeforeBuild)]);
    }

    [Fact]
    public void Parse_ReadsDoubleQuotedAttributes()
    {
        var comment = VsBindingCommentReader.Parse("""/// <binding AfterBuild="bundle" />""");

        comment!.Bindings.ShouldBe([new VsBinding("bundle", TaskBindingKind.AfterBuild)]);
    }

    /// <summary>VS's "Clean" is what IDE++ calls Before Clean; there is no VS attribute for After Clean.</summary>
    [Fact]
    public void Parse_MapsCleanToBeforeClean()
    {
        var comment = VsBindingCommentReader.Parse("/// <binding Clean='clean' />");

        comment!.Bindings.ShouldBe([new VsBinding("clean", TaskBindingKind.BeforeClean)]);
    }

    [Fact]
    public void Parse_ReadsSeveralAttributesAtOnce()
    {
        var comment = VsBindingCommentReader.Parse("/// <binding BeforeBuild='sass' AfterBuild='bundle' Clean='clean' />");

        comment!.Bindings.ShouldBe([
            new VsBinding("sass", TaskBindingKind.BeforeBuild),
            new VsBinding("bundle", TaskBindingKind.AfterBuild),
            new VsBinding("clean", TaskBindingKind.BeforeClean),
        ]);
    }

    [Fact]
    public void Parse_SplitsCommaSeparatedTaskLists()
    {
        var comment = VsBindingCommentReader.Parse("/// <binding BeforeBuild='sass, less , concat' />");

        comment!.Bindings.Select(b => b.TaskName).ShouldBe(["sass", "less", "concat"]);
        comment.Bindings.ShouldAllBe(b => b.Kind == TaskBindingKind.BeforeBuild);
    }

    /// <summary>Reported rather than dropped: IDE++ has no project-open event, and a task that
    /// silently never runs is worse than one the user is told about.</summary>
    [Fact]
    public void Parse_ReportsProjectOpenAsUnsupported()
    {
        var comment = VsBindingCommentReader.Parse("/// <binding ProjectOpen='watch' BeforeBuild='sass' />");

        comment!.UnsupportedProjectOpenTasks.ShouldBe(["watch"]);
        comment.Bindings.ShouldBe([new VsBinding("sass", TaskBindingKind.BeforeBuild)]);
    }

    [Fact]
    public void Parse_UnknownAttributesAreIgnored()
    {
        var comment = VsBindingCommentReader.Parse("/// <binding Whenever='x' BeforeBuild='sass' />");

        comment!.Bindings.ShouldBe([new VsBinding("sass", TaskBindingKind.BeforeBuild)]);
    }

    [Fact]
    public void Parse_EmptyBindingElement_YieldsNoBindings()
    {
        var comment = VsBindingCommentReader.Parse("/// <binding />");

        comment.ShouldNotBeNull();
        comment.Bindings.ShouldBeEmpty();
        comment.UnsupportedProjectOpenTasks.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_FindsTheCommentBelowAHeader()
    {
        var comment = VsBindingCommentReader.Parse("""
            // Copyright someone
            // SPDX-License-Identifier: MIT

            /// <binding BeforeBuild='sass' />
            var gulp = require('gulp');
            """);

        comment!.Bindings.ShouldBe([new VsBinding("sass", TaskBindingKind.BeforeBuild)]);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull() =>
        VsBindingCommentReader.Read(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid()}.js")).ShouldBeNull();

    [Fact]
    public void Read_ReadsFromDisk()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteFile("gulpfile.js", """
            /// <binding BeforeBuild='sass' Clean='clean' />
            var gulp = require('gulp');
            """);

        var comment = VsBindingCommentReader.Read(path);

        comment!.Bindings.ShouldBe([
            new VsBinding("sass", TaskBindingKind.BeforeBuild),
            new VsBinding("clean", TaskBindingKind.BeforeClean),
        ]);
    }

    /// <summary>The comment is a top-of-file convention; scanning a whole bundled file for one that
    /// is not there is wasted work.</summary>
    [Fact]
    public void Read_IgnoresACommentFarDownTheFile()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteFile(
            "gulpfile.js",
            string.Join('\n', Enumerable.Repeat("// filler", 50).Append("/// <binding BeforeBuild='sass' />")));

        VsBindingCommentReader.Read(path).ShouldBeNull();
    }
}
