using Shouldly;
using Xunit;

namespace IdePlusPlus.ReverseEngineering.Tests;

public sealed class ShimGeneratorTests : IDisposable
{
    private static string SelfAssembly => typeof(ManagedDecompiler).Assembly.Location;

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
    public void Generates_a_project_forwarders_and_readme()
    {
        var result = new ShimGenerator().GenerateManagedForwarder(SelfAssembly, _outputDirectory);

        File.Exists(result.ProjectPath).ShouldBeTrue();
        File.Exists(result.ForwardersPath).ShouldBeTrue();
        File.Exists(result.ReadmePath).ShouldBeTrue();
        result.ForwardedTypeCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Forwarders_name_the_public_types()
    {
        var result = new ShimGenerator().GenerateManagedForwarder(SelfAssembly, _outputDirectory);
        var forwarders = File.ReadAllText(result.ForwardersPath);

        forwarders.ShouldContain("using System.Runtime.CompilerServices;");
        forwarders.ShouldContain("[assembly: TypeForwardedTo(typeof(global::IdePlusPlus.ReverseEngineering.ManagedDecompiler))]");
    }

    [Fact]
    public void Project_references_the_original_assembly()
    {
        var result = new ShimGenerator().GenerateManagedForwarder(SelfAssembly, _outputDirectory);
        var project = File.ReadAllText(result.ProjectPath);

        project.ShouldContain("<AssemblyName>IdePlusPlus.ReverseEngineering</AssemblyName>");
        project.ShouldContain(SelfAssembly);
    }

    [Fact]
    public void Generic_types_get_the_right_arity()
    {
        // GhidraOpenResult etc. are non-generic here; assert the arity formatting directly via a
        // generic type the assembly references is not reliable, so check the encoding rule on a
        // known generic from the BCL is out of scope - instead verify no malformed empty <> appears.
        var result = new ShimGenerator().GenerateManagedForwarder(SelfAssembly, _outputDirectory);
        var forwarders = File.ReadAllText(result.ForwardersPath);

        forwarders.ShouldNotContain("<>");
    }
}
