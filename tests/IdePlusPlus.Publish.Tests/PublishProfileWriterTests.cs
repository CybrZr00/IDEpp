using IdePlusPlus.Publish;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Publish.Tests;

public sealed class PublishProfileWriterTests
{
    private const string ExistingProfile = """
        <Project>
          <!--
            Produces a self-contained single-file IdePlusPlus.exe that runs on a machine with no
            .NET runtime installed.
          -->
          <PropertyGroup>
            <Configuration>Release</Configuration>
            <Platform>Any CPU</Platform>
            <PublishDir>bin\Release\net10.0-windows\win-x64\publish\</PublishDir>
            <PublishProtocol>FileSystem</PublishProtocol>
            <RuntimeIdentifier>win-x64</RuntimeIdentifier>
            <SelfContained>true</SelfContained>
            <PublishSingleFile>true</PublishSingleFile>
            <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
            <PublishReadyToRun>false</PublishReadyToRun>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>The single most important guarantee: opening someone's profile and saving it back
    /// unchanged must not change what it means.</summary>
    [Fact]
    public void ToXml_SavingAnUnchangedProfile_RoundTripsEveryProperty()
    {
        var original = PublishProfileReader.Parse(ExistingProfile, "win-x64")!;

        var reread = PublishProfileReader.Parse(PublishProfileWriter.ToXml(original, ExistingProfile), "win-x64")!;

        // Compared apart from AdditionalProperties because a record's generated equality compares that
        // dictionary by reference, which would pass for any two profiles that happened to share one.
        reread.AdditionalProperties.OrderBy(p => p.Key).ShouldBe(original.AdditionalProperties.OrderBy(p => p.Key));
        (reread with { AdditionalProperties = original.AdditionalProperties }).ShouldBe(original);
    }

    [Fact]
    public void ToXml_EditingAnExistingProfile_KeepsItsCommentsAndUnmodelledProperties()
    {
        var original = PublishProfileReader.Parse(ExistingProfile, "win-x64")!;

        var xml = PublishProfileWriter.ToXml(original with { RuntimeIdentifier = "linux-x64" }, ExistingProfile);

        xml.ShouldContain("Produces a self-contained single-file IdePlusPlus.exe");
        xml.ShouldContain("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>");
        xml.ShouldContain("<Platform>Any CPU</Platform>");
        xml.ShouldContain("<RuntimeIdentifier>linux-x64</RuntimeIdentifier>");
        xml.ShouldNotContain("<RuntimeIdentifier>win-x64</RuntimeIdentifier>");
    }

    [Fact]
    public void ToXml_ANewFolderProfile_ProducesAProfileTheReaderUnderstands()
    {
        var profile = PublishProfile.CreateFolderDefault("linux", "net10.0") with
        {
            RuntimeIdentifier = "linux-x64",
            SelfContained = true,
            PublishTrimmed = true,
        };

        var reread = PublishProfileReader.Parse(PublishProfileWriter.ToXml(profile), "linux")!;

        reread.Kind.ShouldBe(PublishKind.Folder);
        reread.TargetFramework.ShouldBe("net10.0");
        reread.RuntimeIdentifier.ShouldBe("linux-x64");
        reread.SelfContained.ShouldBe(true);
        reread.PublishTrimmed.ShouldBeTrue();
    }

    [Fact]
    public void ToXml_ANewContainerProfile_RoundTripsThroughTheKindMarker()
    {
        var profile = PublishProfile.CreateContainerDefault("container", "MyApp", "net10.0") with
        {
            ContainerRegistry = "registry.example.com",
        };

        var xml = PublishProfileWriter.ToXml(profile);
        var reread = PublishProfileReader.Parse(xml, "container")!;

        xml.ShouldContain("<IdePlusPlusPublishKind>Container</IdePlusPlusPublishKind>");
        reread.Kind.ShouldBe(PublishKind.Container);
        reread.ContainerRepository.ShouldBe("myapp");
        reread.ContainerImageTag.ShouldBe("latest");
        reread.ContainerRegistry.ShouldBe("registry.example.com");
    }

    [Fact]
    public void ToXml_ClearingAProperty_RemovesItFromTheFile()
    {
        var original = PublishProfileReader.Parse(ExistingProfile, "win-x64")!;

        var xml = PublishProfileWriter.ToXml(original with { RuntimeIdentifier = null, SelfContained = null }, ExistingProfile);

        xml.ShouldNotContain("<RuntimeIdentifier>");
        xml.ShouldNotContain("<SelfContained>");
    }

    [Fact]
    public void ToXml_SwitchingAFolderProfileToContainer_DropsTheFolderOnlyProperties()
    {
        var original = PublishProfileReader.Parse(ExistingProfile, "win-x64")!;

        var xml = PublishProfileWriter.ToXml(
            original with { Kind = PublishKind.Container, ContainerRepository = "myapp" }, ExistingProfile);

        xml.ShouldNotContain("<PublishSingleFile>");
        xml.ShouldContain("<ContainerRepository>myapp</ContainerRepository>");
    }

    /// <summary>An MSDeploy profile carries settings IDE++ never read. Writing it would delete them,
    /// so the writer refuses instead - the dialog shows those profiles read-only for this reason.</summary>
    [Fact]
    public void ToXml_AnUnsupportedProfile_Throws()
    {
        var profile = PublishProfileReader.Parse(
            "<Project><PropertyGroup><WebPublishMethod>MSDeploy</WebPublishMethod></PropertyGroup></Project>",
            "azure")!;

        Should.Throw<ArgumentException>(() => PublishProfileWriter.ToXml(profile));
    }

    [Fact]
    public void ToXml_AProfileWithAnXmlDeclaration_KeepsIt()
    {
        const string withDeclaration = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project><PropertyGroup><Configuration>Release</Configuration></PropertyGroup></Project>
            """;

        var profile = PublishProfileReader.Parse(withDeclaration, "vs")!;

        PublishProfileWriter.ToXml(profile, withDeclaration).ShouldStartWith("<?xml");
    }

    [Fact]
    public void ToXml_AProfileWithoutAnXmlDeclaration_DoesNotGainOne()
    {
        var profile = PublishProfileReader.Parse(ExistingProfile, "win-x64")!;

        PublishProfileWriter.ToXml(profile, ExistingProfile).ShouldStartWith("<Project>");
    }

    /// <summary>Legacy VS profiles declare the 2003 MSBuild namespace; a new element added to one has
    /// to go into that namespace too or MSBuild stops seeing it.</summary>
    [Fact]
    public void ToXml_AddingAPropertyToANamespacedProfile_UsesTheDocumentsNamespace()
    {
        const string legacy = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><Configuration>Release</Configuration></PropertyGroup>
            </Project>
            """;

        var profile = PublishProfileReader.Parse(legacy, "FolderProfile")!;

        var xml = PublishProfileWriter.ToXml(profile with { RuntimeIdentifier = "win-x64" }, legacy);

        xml.ShouldContain("<RuntimeIdentifier>win-x64</RuntimeIdentifier>");
        xml.ShouldNotContain("xmlns=\"\"");
        PublishProfileReader.Parse(xml, "FolderProfile")!.RuntimeIdentifier.ShouldBe("win-x64");
    }
}
