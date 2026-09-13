using AccessVbaPlugin.Symbols;
using Shouldly;
using Xunit;

namespace AccessVbaPlugin.Tests;

public sealed class VbaCompletionEngineTests
{
    private static readonly VbaModule Helpers = VbaModuleParser.Parse("Helpers", VbaModuleKind.Standard, """
        Public gLimit As Long
        Private secret As Long
        Public Enum Colour
            clRed = 1
            clBlue = 2
        End Enum
        Public Function GetDb() As DAO.Database
        End Function
        Private Sub Hidden()
        End Sub
        """);

    private static readonly VbaModule Stock = VbaModuleParser.Parse("clsStock", VbaModuleKind.Class, """
        Private mQty As Long
        Public Property Get Quantity() As Long
        End Property
        Public Property Let Quantity(value As Long)
        End Property
        Public Sub Reserve(ByVal amount As Long)
        End Sub
        """);

    private const string CurrentText = """
        Option Explicit
        Private mStock As clsStock
        Public Sub Work(ByVal id As Long)
            Dim rs As DAO.Recordset
            Dim item As clsStock
            Set item = New clsStock
            item.
            With rs
                .MoveFirst
                .
            End With
            DoCmd.
            Helpers.
            Colour.
            Get
        End Sub
        """;

    private static VbaCompletionEngine Engine()
    {
        var index = new VbaSymbolIndex();
        index.Upsert(Helpers);
        index.Upsert(Stock);
        return new VbaCompletionEngine(index, VbaLanguage.Minimal);
    }

    private static readonly VbaModule Current = VbaModuleParser.Parse("Form_Orders", VbaModuleKind.Document, CurrentText);

    private static int OffsetAfter(string marker)
    {
        var index = CurrentText.IndexOf(marker, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, marker);
        return index + marker.Length;
    }

    [Fact]
    public void MemberCompletionOnAClassModuleVariableListsItsPublicMembersOnly()
    {
        var items = Engine().GetCompletions(Current, CurrentText, OffsetAfter("    item."), out _);

        items.Select(i => i.Text).ShouldBe(["Quantity", "Reserve"]);
        items.Single(i => i.Text == "Quantity").Kind.ShouldBe(VbaCompletionKind.Property);
        items.Select(i => i.Text).ShouldNotContain("mQty");
    }

    [Fact]
    public void MemberCompletionOnAWellKnownTypeAndInsideAWithBlock()
    {
        var engine = Engine();

        engine.GetCompletions(Current, CurrentText, OffsetAfter("DoCmd."), out _).Select(i => i.Text).ShouldContain("OpenForm");
        engine.GetCompletions(Current, CurrentText, OffsetAfter("        ."), out _).Select(i => i.Text).ShouldContain("MoveNext");
    }

    [Fact]
    public void MemberCompletionOnAStandardModuleAndAnEnum()
    {
        var engine = Engine();

        var helpers = engine.GetCompletions(Current, CurrentText, OffsetAfter("Helpers."), out _).Select(i => i.Text).ToList();
        helpers.ShouldContain("GetDb");
        helpers.ShouldContain("gLimit");
        helpers.ShouldNotContain("Hidden");
        helpers.ShouldNotContain("secret");

        engine.GetCompletions(Current, CurrentText, OffsetAfter("Colour."), out _).Select(i => i.Text).ShouldBe(["clBlue", "clRed"]);
    }

    [Fact]
    public void GlobalCompletionPrefersLocalsThenModuleThenProjectAndFiltersByPrefix()
    {
        var items = Engine().GetCompletions(Current, CurrentText, OffsetAfter("    Get"), out var replaceStart);

        replaceStart.ShouldBe(OffsetAfter("    Get") - 3);
        items.Select(i => i.Text).ShouldContain("GetDb");
        items.ShouldAllBe(i => i.Text.StartsWith("Get", StringComparison.OrdinalIgnoreCase));

        var all = Engine().GetCompletions(Current, CurrentText, OffsetAfter("    Get") - 3, out _);
        all.First().Kind.ShouldBe(VbaCompletionKind.Parameter);
        all.Select(i => i.Text).ShouldContain("id");
        all.Select(i => i.Text).ShouldContain("rs");
        all.Select(i => i.Text).ShouldContain("mStock");
        all.Select(i => i.Text).ShouldContain("Me");
        all.Select(i => i.Text).ShouldContain("clsStock");
        all.Select(i => i.Text).ShouldContain("clRed");
        all.Select(i => i.Text).ShouldContain("MsgBox");
    }

    [Fact]
    public void NoCompletionInsideStringsOrComments()
    {
        const string text = "Sub S()\r\nx = \"DoCmd.\r\n' DoCmd.\r\nEnd Sub";
        var module = VbaModuleParser.Parse("M", VbaModuleKind.Standard, text);
        var engine = Engine();

        engine.GetCompletions(module, text, text.IndexOf("\"DoCmd.", StringComparison.Ordinal) + 7, out _).ShouldBeEmpty();
        engine.GetCompletions(module, text, text.IndexOf("' DoCmd.", StringComparison.Ordinal) + 8, out _).ShouldBeEmpty();
    }

    [Fact]
    public void HoverDescribesLocalsProceduresAndMembers()
    {
        var engine = Engine();

        engine.GetHover(Current, CurrentText, CurrentText.IndexOf("With rs", StringComparison.Ordinal) + 5)!.Text.ShouldBe("Dim rs As DAO.Recordset - local in Work");
        engine.GetHover(Current, CurrentText, CurrentText.IndexOf("Helpers.", StringComparison.Ordinal))!.Text.ShouldContain("Standard module Helpers");
        engine.GetHover(Current, CurrentText, CurrentText.IndexOf("MoveFirst", StringComparison.Ordinal))!.Text.ShouldBe("MoveFirst - Recordset member");
        engine.GetHover(Current, CurrentText, CurrentText.IndexOf("Explicit", StringComparison.Ordinal)).ShouldBeNull();
    }

    [Fact]
    public void MeInADocumentModuleOffersItsOwnMembersPlusFormMembers()
    {
        const string text = "Private mX As Long\r\nPrivate Sub Form_Load()\r\n    Me.\r\nEnd Sub";
        var module = VbaModuleParser.Parse("Form_Orders", VbaModuleKind.Document, text);
        var items = Engine().GetCompletions(module, text, text.IndexOf("Me.", StringComparison.Ordinal) + 3, out _).Select(i => i.Text).ToList();

        items.ShouldContain("mX");
        items.ShouldContain("Form_Load");
        items.ShouldContain("Requery");
    }
}
