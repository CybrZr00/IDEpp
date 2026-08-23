using Shouldly;
using Xunit;

namespace IdePlusPlus.TestRunner.Tests;

/// <summary>
/// Exercises TestDiscoveryService against a real scratch xunit project via the actual
/// `dotnet test --list-tests` CLI. The scratch project deliberately uses a multi-segment namespace
/// (dots before the class name) to verify the "split on the LAST dot" heuristic correctly separates
/// class-with-dotted-namespace from method name, rather than blowing up on the namespace's own dots.
/// </summary>
public sealed class TestDiscoveryServiceTests : IDisposable
{
    private const string SourceCode = """
        using Xunit;

        namespace My.Nested.Namespace;

        public class FirstTests
        {
            [Fact]
            public void MethodOne()
            {
            }

            [Fact]
            public void MethodTwo()
            {
            }
        }

        public class SecondTests
        {
            [Fact]
            public void MethodThree()
            {
            }
        }
        """;

    private readonly ScratchXunitProject _project = new("DiscoveryFixture", "SampleTests.cs", SourceCode);
    private readonly TestDiscoveryService _discoveryService = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task DiscoverAsync_OnAScratchProjectWithThreeTestsAcrossTwoClasses_FindsAllThree()
    {
        var cases = await _discoveryService.DiscoverAsync(_project.CsprojPath);

        cases.Count.ShouldBe(3);
        cases.ShouldAllBe(c => c.ProjectPath == _project.CsprojPath);
    }

    [Fact]
    public async Task DiscoverAsync_SplitsTheDottedNamespaceAndClassFromTheMethodNameCorrectly()
    {
        var cases = await _discoveryService.DiscoverAsync(_project.CsprojPath);

        var methodOne = cases.Single(c => c.MethodName == "MethodOne");
        methodOne.ClassName.ShouldBe("My.Nested.Namespace.FirstTests");
        methodOne.FullyQualifiedName.ShouldBe("My.Nested.Namespace.FirstTests.MethodOne");

        var methodThree = cases.Single(c => c.MethodName == "MethodThree");
        methodThree.ClassName.ShouldBe("My.Nested.Namespace.SecondTests");
        methodThree.FullyQualifiedName.ShouldBe("My.Nested.Namespace.SecondTests.MethodThree");
    }
}
