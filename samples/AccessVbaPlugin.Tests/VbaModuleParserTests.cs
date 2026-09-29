using System.IO;
using AccessVbaPlugin.Symbols;
using Shouldly;
using Xunit;

namespace AccessVbaPlugin.Tests;

public sealed class VbaModuleParserTests
{
    private const string Sample = """
        Option Compare Database
        Option Explicit

        Private Const MAX_ROWS As Long = 100, PREFIX = "x"
        Public gCount As Long, gNames() As String
        Private WithEvents mForm As Form
        Dim mDb As DAO.Database

        Public Enum OrderStatus
            osNew = 1
            osShipped
        End Enum

        Private Type Address
            Street As String
            Post As String
        End Type

        Public Event Changed(ByVal id As Long)

        Public Declare PtrSafe Function GetTickCount Lib "kernel32" () As Long

        ' A comment with Sub Fake() in it
        Public Function FindOrder(ByVal orderId As Long, Optional includeLines As Boolean = False, _
                                  ParamArray extras() As Variant) As DAO.Recordset
            Dim rs As DAO.Recordset, i As Integer
            Static hits As Long
            Const LOCAL_LIMIT = 5
            Set rs = mDb.OpenRecordset("Orders") ' trailing comment: Dim notAVar As Long
            Set FindOrder = rs
        End Function

        Private Sub Log(msg$)
            Debug.Print msg
        End Sub

        Property Get Count() As Long
            Count = gCount
        End Property

        Property Let Count(value As Long)
            gCount = value
        End Property
        """;

    [Fact]
    public void ParsesModuleLevelDeclarations()
    {
        var module = VbaModuleParser.Parse("Sample", VbaModuleKind.Standard, Sample);

        module.Fields.Select(f => f.Name).ShouldBe(["MAX_ROWS", "PREFIX", "gCount", "gNames", "mForm", "mDb"]);
        module.Fields.Single(f => f.Name == "MAX_ROWS").ShouldSatisfyAllConditions(
            f => f.IsConst.ShouldBeTrue(),
            f => f.Type.ShouldBe("Long"),
            f => f.Visibility.ShouldBe(VbaVisibility.Private));
        module.Fields.Single(f => f.Name == "gNames").ShouldSatisfyAllConditions(
            f => f.IsArray.ShouldBeTrue(),
            f => f.Visibility.ShouldBe(VbaVisibility.Public));
        module.Fields.Single(f => f.Name == "mForm").Type.ShouldBe("Form");
        module.Fields.Single(f => f.Name == "mDb").Type.ShouldBe("DAO.Database");
    }

    [Fact]
    public void ParsesEnumsTypesEventsAndDeclares()
    {
        var module = VbaModuleParser.Parse("Sample", VbaModuleKind.Standard, Sample);

        var status = module.Enums.ShouldHaveSingleItem();
        status.Name.ShouldBe("OrderStatus");
        status.Members.Select(m => (m.Name, m.Value)).ShouldBe([("osNew", "1"), ("osShipped", null)]);

        var address = module.Types.ShouldHaveSingleItem();
        address.Name.ShouldBe("Address");
        address.Visibility.ShouldBe(VbaVisibility.Private);
        address.Fields.Select(f => f.Name).ShouldBe(["Street", "Post"]);

        module.Procedures.Single(p => p.Name == "Changed").Kind.ShouldBe(VbaProcedureKind.Event);
        var tick = module.Procedures.Single(p => p.Name == "GetTickCount");
        tick.Kind.ShouldBe(VbaProcedureKind.DeclareFunction);
        tick.ReturnType.ShouldBe("Long");
    }

    [Fact]
    public void ParsesProceduresWithContinuationsParametersAndLocals()
    {
        var module = VbaModuleParser.Parse("Sample", VbaModuleKind.Standard, Sample);

        var find = module.Procedures.Single(p => p.Name == "FindOrder");
        find.Kind.ShouldBe(VbaProcedureKind.Function);
        find.Visibility.ShouldBe(VbaVisibility.Public);
        find.ReturnType.ShouldBe("DAO.Recordset");
        find.Parameters.Select(p => p.Name).ShouldBe(["orderId", "includeLines", "extras"]);
        find.Parameters[0].ByVal.ShouldBeTrue();
        find.Parameters[1].ShouldSatisfyAllConditions(
            p => p.IsOptional.ShouldBeTrue(),
            p => p.DefaultValue.ShouldBe("False"));
        find.Parameters[2].ShouldSatisfyAllConditions(
            p => p.IsParamArray.ShouldBeTrue(),
            p => p.IsArray.ShouldBeTrue());
        find.Locals.Select(l => l.Name).ShouldBe(["rs", "i", "hits", "LOCAL_LIMIT"]);
        find.Locals.Single(l => l.Name == "LOCAL_LIMIT").IsConst.ShouldBeTrue();
        find.Locals.ShouldNotContain(l => l.Name == "notAVar");
        find.StartLine.ShouldBe(24);
        find.EndLine.ShouldBe(31);
        find.Signature.ShouldBe("Public Function FindOrder(ByVal orderId As Long, Optional includeLines As Boolean = False, ParamArray extras() As Variant) As DAO.Recordset");

        module.Procedures.ShouldNotContain(p => p.Name == "Fake");

        var log = module.Procedures.Single(p => p.Name == "Log");
        log.Visibility.ShouldBe(VbaVisibility.Private);
        log.Parameters.Single().Type.ShouldBe("String");

        module.Procedures.Where(p => p.Name == "Count").Select(p => p.Kind)
            .ShouldBe([VbaProcedureKind.PropertyGet, VbaProcedureKind.PropertyLet]);

        module.ProcedureAtLine(27)!.Name.ShouldBe("FindOrder");
        module.ProcedureAtLine(5).ShouldBeNull();
    }

    [Fact]
    public void AcceptsARawExportFileWithHeaders()
    {
        const string exported = "VERSION 1.0 CLASS\r\nBEGIN\r\n  MultiUse = -1  'True\r\nEND\r\nAttribute VB_Name = \"busCustomer\"\r\nAttribute VB_Exposed = False\r\nOption Compare Database\r\n\r\nPublic CustomerID As Long\r\nPublic lOrderID As Long\r\n";
        var module = VbaModuleParser.Parse("busCustomer", VbaModuleKind.Class, exported);

        module.Fields.Select(f => f.Name).ShouldBe(["CustomerID", "lOrderID"]);
        module.Procedures.ShouldBeEmpty();
    }

    [Fact]
    public void SplitsStatementsOnColonsButNotInsideStringsOrParens()
    {
        VbaModuleParser.SplitTopLevel("a As Long, b(1, 2) As String, c = \"x,y\"", ',')
            .ShouldBe(["a As Long", " b(1, 2) As String", " c = \"x,y\""]);

        var module = VbaModuleParser.Parse("M", VbaModuleKind.Standard, "Sub S()\r\nDim a As Long: Dim b As String: x = \"a:b\"\r\nEnd Sub");
        module.Procedures.Single().Locals.Select(l => l.Name).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void NeverThrowsOnOddInput()
    {
        foreach (var text in new[] { "", "Sub", "Sub (", "Dim", "Public Function ( As", "Type\r\nEnd Type", "Enum X", "With\r\n.Foo", "\"unterminated", "' only a comment", "Sub A()\r\nSub B()\r\nEnd Sub" })
        {
            Should.NotThrow(() => VbaModuleParser.Parse("M", VbaModuleKind.Standard, text));
        }
    }

    [Fact]
    public void ParsesTheRealExportCorpusWhenPresent()
    {
        // The plugin creates the folder before its first export, so an existing-but-empty folder
        // (an export that never completed) must count as "corpus not present", not as a failure.
        const string folder = @"C:\FAM\_vba_export";
        if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "*.bas").Any())
        {
            return;
        }

        var index = VbaSymbolIndex.FromExportFolder(folder);

        index.Modules.Count.ShouldBeGreaterThan(1000);
        index.GetModule("Checks")!.Procedures.Select(p => p.Name).ShouldBe(["CheckRefs", "FixUpRefs"]);
        index.GetModule("busCustomer")!.ShouldSatisfyAllConditions(
            m => m.Kind.ShouldBe(VbaModuleKind.Class),
            m => m.Fields.Select(f => f.Name).ShouldBe(["CustomerID", "lOrderID"]));
        index.Modules.Sum(m => m.Procedures.Count).ShouldBeGreaterThan(1000);
    }
}
