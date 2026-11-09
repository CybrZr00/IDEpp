using Shouldly;
using Xunit;

namespace IdePlusPlus.ReverseEngineering.Tests;

public sealed class ShimGeneratorTests : IDisposable
{
    private static string SelfAssembly => typeof(ManagedDecompiler).Assembly.Location;
    private const string NativeDll = @"C:\Windows\System32\kernel32.dll";

    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusShimTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void Managed_generates_a_project_forwarders_and_readme()
    {
        var result = new ShimGenerator().GenerateManagedForwarder(SelfAssembly, _outputDirectory);

        result.ForwardedCount.ShouldBeGreaterThan(0);
        result.PrimaryPath.ShouldEndWith(".csproj");
        result.Files.ShouldContain(f => f.EndsWith("Forwarders.cs"));
        result.Files.ShouldContain(f => f.EndsWith("README.md"));
        result.Files.ShouldAllBe(f => File.Exists(f));
    }

    [Fact]
    public void Managed_forwarders_name_the_public_types()
    {
        var result = new ShimGenerator().GenerateManagedForwarder(SelfAssembly, _outputDirectory);
        var forwarders = File.ReadAllText(result.Files.First(f => f.EndsWith("Forwarders.cs")));

        forwarders.ShouldContain("[assembly: TypeForwardedTo(typeof(global::IdePlusPlus.ReverseEngineering.ManagedDecompiler))]");
        forwarders.ShouldNotContain("<>");
    }

    [Fact]
    public void Native_generates_a_def_that_forwards_exports()
    {
        var result = new ShimGenerator().GenerateNativeForwarder(NativeDll, _outputDirectory);

        result.ForwardedCount.ShouldBeGreaterThan(0);
        result.PrimaryPath.ShouldEndWith(".def");

        var def = File.ReadAllText(result.PrimaryPath);
        def.ShouldContain("LIBRARY kernel32");
        def.ShouldContain("EXPORTS");
        def.ShouldContain("CreateFileW = kernel32_orig.CreateFileW");
    }

    [Fact]
    public void GenerateForwarder_detects_managed_vs_native()
    {
        new ShimGenerator().GenerateForwarder(SelfAssembly, Path.Combine(_outputDirectory, "m"))
            .PrimaryPath.ShouldEndWith(".csproj");

        new ShimGenerator().GenerateForwarder(NativeDll, Path.Combine(_outputDirectory, "n"))
            .PrimaryPath.ShouldEndWith(".def");
    }
}
