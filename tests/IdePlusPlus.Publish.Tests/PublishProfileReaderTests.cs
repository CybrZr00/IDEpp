using IdePlusPlus.Publish;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Publish.Tests;

public sealed class PublishProfileReaderTests
{
    /// <summary>Byte-for-byte the shape of this repo's own src/IdePlusPlus.App/Properties/
    /// PublishProfiles/win-x64.pubxml - the profile the feature has to read on day one.</summary>
    private const string RepoOwnProfile = """
        <Project>
          <!-- Produces a self-contained single-file IdePlusPlus.exe. -->
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

    [Fact]
    public void Parse_AFileSystemProfile_ReadsEveryModelledProperty()
    {
        var profile = PublishProfileReader.Parse(RepoOwnProfile, "win-x64");

        profile.ShouldNotBeNull();
        profile.Name.ShouldBe("win-x64");
        profile.Kind.ShouldBe(PublishKind.Folder);
        profile.Configuration.ShouldBe("Release");
        profile.RuntimeIdentifier.ShouldBe("win-x64");
        profile.SelfContained.ShouldBe(true);
        profile.PublishSingleFile.ShouldBeTrue();
        profile.PublishReadyToRun.ShouldBeFalse();
        profile.PublishTrimmed.ShouldBeFalse();
        profile.PublishDir.ShouldBe(@"bin\Release\net10.0-windows\win-x64\publish\");
    }

    [Fact]
    public void Parse_PropertiesItDoesNotModel_KeepsThemInAdditionalProperties()
    {
        var profile = PublishProfileReader.Parse(RepoOwnProfile, "win-x64");

        profile.ShouldNotBeNull();
        profile.AdditionalProperties["Platform"].ShouldBe("Any CPU");
        profile.AdditionalProperties["IncludeNativeLibrariesForSelfExtract"].ShouldBe("true");
        profile.AdditionalProperties.ShouldNotContainKey("RuntimeIdentifier");
    }

    /// <summary>Profiles written by older Visual Studio versions declare the 2003 MSBuild namespace
    /// on the root element. Matching on local names has to read them identically.</summary>
    [Fact]
    public void Parse_ALegacyNamespacedProfile_ReadsItTheSameAsAnSdkStyleOne()
    {
        const string legacy = """
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <WebPublishMethod>FileSystem</WebPublishMethod>
                <LastUsedBuildConfiguration>Release</LastUsedBuildConfiguration>
                <Configuration>Release</Configuration>
                <TargetFramework>net8.0</TargetFramework>
                <publishUrl>bin\Release\net8.0\publish\</publishUrl>
              </PropertyGroup>
            </Project>
            """;

        var profile = PublishProfileReader.Parse(legacy, "FolderProfile");

        profile.ShouldNotBeNull();
        profile.Kind.ShouldBe(PublishKind.Folder);
        profile.Configuration.ShouldBe("Release");
        profile.TargetFramework.ShouldBe("net8.0");
        profile.AdditionalProperties["LastUsedBuildConfiguration"].ShouldBe("Release");
        profile.AdditionalProperties["WebPublishMethod"].ShouldBe("FileSystem");
    }

    [Fact]
    public void Parse_AProfileWithNoProtocol_IsAFolderPublish()
    {
        var profile = PublishProfileReader.Parse(
            "<Project><PropertyGroup><Configuration>Debug</Configuration></PropertyGroup></Project>", "bare");

        profile.ShouldNotBeNull();
        profile.Kind.ShouldBe(PublishKind.Folder);
        profile.Configuration.ShouldBe("Debug");
    }

    [Theory]
    [InlineData("MSDeploy")]
    [InlineData("FTP")]
    [InlineData("AzureWebSite")]
    public void Parse_AProtocolItCannotRun_IsUnsupportedRatherThanRejected(string protocol)
    {
        var profile = PublishProfileReader.Parse(
            $"<Project><PropertyGroup><WebPublishMethod>{protocol}</WebPublishMethod></PropertyGroup></Project>",
            "azure");

        profile.ShouldNotBeNull();
        profile.Kind.ShouldBe(PublishKind.Unsupported);
        profile.IsRunnable.ShouldBeFalse();
    }

    [Fact]
    public void Parse_AContainerProfile_ReadsTheKindMarkerAndImageSettings()
    {
        const string container = """
            <Project>
              <PropertyGroup>
                <PublishProtocol>FileSystem</PublishProtocol>
                <IdePlusPlusPublishKind>Container</IdePlusPlusPublishKind>
                <Configuration>Release</Configuration>
                <ContainerRepository>myapp</ContainerRepository>
                <ContainerImageTag>1.2.3</ContainerImageTag>
                <ContainerRegistry>registry.example.com</ContainerRegistry>
                <ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:10.0</ContainerBaseImage>
              </PropertyGroup>
            </Project>
            """;

        var profile = PublishProfileReader.Parse(container, "container");

        profile.ShouldNotBeNull();
        profile.Kind.ShouldBe(PublishKind.Container);
        profile.ContainerRepository.ShouldBe("myapp");
        profile.ContainerImageTag.ShouldBe("1.2.3");
        profile.ContainerRegistry.ShouldBe("registry.example.com");
        profile.ContainerBaseImage.ShouldBe("mcr.microsoft.com/dotnet/aspnet:10.0");
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsNullRatherThanThrowing() =>
        PublishProfileReader.Parse("<Project><PropertyGroup>", "broken").ShouldBeNull();

    [Fact]
    public void Parse_AFileThatIsNotAProfile_ReturnsNull() =>
        PublishProfileReader.Parse("<configuration><appSettings /></configuration>", "app.config").ShouldBeNull();

    /// <summary>MSBuild takes the last definition of a property, so the reader has to as well.</summary>
    [Fact]
    public void Parse_APropertyDefinedTwice_TakesTheLastOne()
    {
        var profile = PublishProfileReader.Parse(
            """
            <Project>
              <PropertyGroup><Configuration>Debug</Configuration></PropertyGroup>
              <PropertyGroup><Configuration>Release</Configuration></PropertyGroup>
            </Project>
            """,
            "twice");

        profile.ShouldNotBeNull();
        profile.Configuration.ShouldBe("Release");
    }

    [Fact]
    public void Parse_AnEmptyProperty_IsTreatedAsAbsent()
    {
        var profile = PublishProfileReader.Parse(
            "<Project><PropertyGroup><RuntimeIdentifier></RuntimeIdentifier></PropertyGroup></Project>", "empty");

        profile.ShouldNotBeNull();
        profile.RuntimeIdentifier.ShouldBeNull();
        profile.SelfContained.ShouldBeNull();
    }
}
