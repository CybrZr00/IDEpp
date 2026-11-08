using Shouldly;
using Xunit;

namespace IdePlusPlus.ReverseEngineering.Tests;

/// <summary>
/// The formatter turns the Ghidra post-script's JSON into readable text. These feed it captured-shape
/// fixtures so the whole translation is covered without running Ghidra.
/// </summary>
public sealed class GhidraResultFormatterTests
{
    [Fact]
    public void Overview_is_formatted()
    {
        var (isError, text) = GhidraResultFormatter.Format("overview",
            """{"ok":true,"name":"foo.dll","languageId":"x86:LE:64:default","compiler":"windows","imageBase":"180000000","functions":1234,"symbols":5678}""");

        isError.ShouldBeFalse();
        text.ShouldContain("foo.dll");
        text.ShouldContain("x86:LE:64:default");
        text.ShouldContain("1234");
        text.ShouldContain("5678");
    }

    [Fact]
    public void Functions_lists_address_and_name()
    {
        var (isError, text) = GhidraResultFormatter.Format("functions",
            """{"ok":true,"total":3,"shown":2,"functions":[{"name":"main","address":"140001000"},{"name":"helper","address":"140002000"}]}""");

        isError.ShouldBeFalse();
        text.ShouldContain("2 of 3 function(s)");
        text.ShouldContain("140001000  main");
        text.ShouldContain("140002000  helper");
    }

    [Fact]
    public void Decompile_shows_a_header_and_the_code()
    {
        var (isError, text) = GhidraResultFormatter.Format("decompile",
            """{"ok":true,"name":"main","address":"140001000","code":"int main(void)\n{\n  return 0;\n}"}""");

        isError.ShouldBeFalse();
        text.ShouldContain("// main @ 140001000");
        text.ShouldContain("int main(void)");
    }

    [Fact]
    public void Xrefs_lists_callers_and_handles_none()
    {
        GhidraResultFormatter.Format("xrefs",
            """{"ok":true,"name":"main","to":[{"from":"140003000","fromFunction":"start"}]}""")
            .Text.ShouldContain("140003000  in start");

        GhidraResultFormatter.Format("xrefs", """{"ok":true,"name":"main","to":[]}""")
            .Text.ShouldContain("(none)");
    }

    [Fact]
    public void Callgraph_joins_callers_and_callees()
    {
        var (_, text) = GhidraResultFormatter.Format("callgraph",
            """{"ok":true,"name":"main","callers":["start"],"callees":["puts","malloc"]}""");

        text.ShouldContain("Callers: start");
        text.ShouldContain("Callees: puts, malloc");
    }

    [Fact]
    public void Rename_and_comment_confirm_persistence()
    {
        GhidraResultFormatter.Format("rename",
            """{"ok":true,"oldName":"FUN_140001000","newName":"main","address":"140001000"}""")
            .Text.ShouldContain("Renamed FUN_140001000 to main");

        GhidraResultFormatter.Format("comment", """{"ok":true,"address":"140001000","comment":"entry"}""")
            .Text.ShouldContain("Comment set at 140001000");
    }

    [Fact]
    public void An_error_result_is_surfaced_as_an_error()
    {
        var (isError, text) = GhidraResultFormatter.Format("decompile", """{"ok":false,"error":"function not found: nope"}""");

        isError.ShouldBeTrue();
        text.ShouldBe("function not found: nope");
    }

    [Fact]
    public void Unparseable_output_is_an_error()
    {
        var (isError, text) = GhidraResultFormatter.Format("overview", "this is not json");

        isError.ShouldBeTrue();
        text.ShouldContain("could not be parsed");
    }
}
