using Shouldly;
using Xunit;

namespace IdePlusPlus.ReverseEngineering.Tests;

/// <summary>
/// Exercises the in-process .NET decompiler against a real assembly - the decompiler library's own
/// assembly, which is guaranteed to be next to the test at runtime. This is the path that recovered
/// the DigitalJeeves/QuestPDF code.
/// </summary>
public sealed class ManagedDecompilerTests
{
    // typeof(ManagedDecompiler).Assembly - a managed assembly that is always present beside the test.
    private static string SelfAssembly => typeof(ManagedDecompiler).Assembly.Location;

    [Fact]
    public void Overview_reads_name_type_count_and_references()
    {
        var overview = new ManagedDecompiler().Overview(SelfAssembly);

        overview.Name.ShouldBe("IdePlusPlus.ReverseEngineering");
        overview.TypeCount.ShouldBeGreaterThan(0);
        overview.References.ShouldContain(r => r.Contains("ICSharpCode.Decompiler"));
    }

    [Fact]
    public void Overview_rejects_a_non_managed_file()
    {
        var textFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(textFile, "not an assembly");
        try
        {
            Should.Throw<InvalidOperationException>(() => new ManagedDecompiler().Overview(textFile));
        }
        finally
        {
            File.Delete(textFile);
        }
    }

    [Fact]
    public void ListTypes_can_filter_by_name()
    {
        var types = new ManagedDecompiler().ListTypes(SelfAssembly, "ManagedDecompiler");

        types.ShouldContain("IdePlusPlus.ReverseEngineering.ManagedDecompiler");
    }

    [Fact]
    public void DecompileType_returns_csharp_for_a_type()
    {
        var code = new ManagedDecompiler().DecompileType(SelfAssembly, "ManagedDecompiler");

        code.ShouldContain("class ManagedDecompiler");
        code.ShouldContain("DecompileType");
    }

    [Fact]
    public void DecompileType_matches_by_full_name_too()
    {
        var code = new ManagedDecompiler().DecompileType(SelfAssembly, "IdePlusPlus.ReverseEngineering.ManagedAssemblyOverview");

        code.ShouldContain("ManagedAssemblyOverview");
    }

    [Fact]
    public void DecompileType_reports_a_missing_type()
    {
        Should.Throw<ArgumentException>(() => new ManagedDecompiler().DecompileType(SelfAssembly, "No.Such.Type"));
    }
}
