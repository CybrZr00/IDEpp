using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

/// <summary>
/// Process.Start fails outright on a working directory that doesn't exist, while `dotnet new` is
/// perfectly happy to create a nested --output whose parents are absent. Adding a class to a folder
/// that didn't exist yet therefore failed with "Could not run dotnet - is the SDK installed and on
/// PATH?", which is both wrong and impossible to act on.
/// </summary>
public class WorkingDirectoryTests
{
    [Fact]
    public void AnExistingDirectoryIsUsedAsIs()
    {
        using var directory = new TempDirectory();

        TemplateInstantiationService.NearestExistingDirectory(directory.Path).ShouldBe(directory.Path);
    }

    [Fact]
    public void AMissingDirectoryWalksUpToItsNearestExistingAncestor()
    {
        using var directory = new TempDirectory();

        TemplateInstantiationService.NearestExistingDirectory(directory.PathFor("Abstractions/Storage"))
            .ShouldBe(directory.Path);
    }

    [Fact]
    public void AnExistingIntermediateDirectoryWins()
    {
        using var directory = new TempDirectory();
        var abstractions = directory.CreateDirectory("Abstractions");

        TemplateInstantiationService.NearestExistingDirectory(directory.PathFor("Abstractions/Storage"))
            .ShouldBe(abstractions);
    }

    /// <summary>A path on a drive that isn't mounted has no existing ancestor at all.</summary>
    [Fact]
    public void APathWithNoExistingAncestorFallsBackToSomethingThatDoesExist() =>
        Directory.Exists(TemplateInstantiationService.NearestExistingDirectory(@"Q:\nowhere\at\all"))
            .ShouldBeTrue();
}
