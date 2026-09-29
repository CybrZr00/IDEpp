using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

/// <summary>
/// Reading only. The cache format belongs to the template engine, not to IDE++, so these pin the
/// shapes against a captured sample: a format change that moves them should be a failing test rather
/// than an empty New Project dialog.
/// </summary>
public class TemplateCacheReaderTests
{
    [Fact]
    public void ReadsEveryEntryInTheSample() =>
        SampleCache.Templates.Count.ShouldBe(12);

    [Fact]
    public void ReadsTheUntruncatedName() =>
        SampleCache.Template("Aspire.Starter.CSharp.13.4").Name
            .ShouldBe("Aspire Starter App (ASP.NET Core/Blazor)");

    [Fact]
    public void ReadsShortNamesClassificationsAndPrecedence()
    {
        var classLibrary = SampleCache.Template("Microsoft.Common.Library.CSharp.10.0");

        classLibrary.ShortNames.ShouldBe(["classlib"]);
        classLibrary.ShortName.ShouldBe("classlib");
        classLibrary.Classifications.ShouldBe(["Common", "Library"]);
        classLibrary.Precedence.ShouldBe(12000);
        classLibrary.Language.ShouldBe("C#");
        classLibrary.Author.ShouldBe("Microsoft");
    }

    [Theory]
    [InlineData("Microsoft.Common.Library.CSharp.10.0", TemplateKind.Project)]
    [InlineData("DotNet.Common.Class.CSharp", TemplateKind.Item)]
    [InlineData("Aspire.Starter.CSharp.13.4", TemplateKind.Solution)]
    public void ReadsTheTypeTagAsAKind(string identity, TemplateKind expected) =>
        SampleCache.Template(identity).Kind.ShouldBe(expected);

    /// <summary>One entry in the installed cache genuinely has no group identity; without the
    /// fallback every such template would collapse into a single "" group.</summary>
    [Fact]
    public void MissingGroupIdentityFallsBackToTheIdentity()
    {
        var editorConfig = SampleCache.Template("Microsoft.Standard.QuickStarts.EditorConfigFile");

        editorConfig.GroupIdentity.ShouldBe("Microsoft.Standard.QuickStarts.EditorConfigFile");
        editorConfig.Language.ShouldBeNull();
    }

    [Fact]
    public void ReadsChoiceParametersWithTheirValues() =>
        SampleCache.Template("Microsoft.Common.Library.CSharp.8.0")
            .FindParameter("Framework")!.Choices.Select(c => c.Value)
            .ShouldBe(["net8.0", "netstandard2.1", "netstandard2.0"]);

    [Fact]
    public void ReadsParameterDataTypes()
    {
        var apiController = SampleCache.Template("Microsoft.AspNetCore.Mvc.ApiController.10.0");

        apiController.FindParameter("actions")!.DataType.ShouldBe(TemplateParameterDataType.Bool);
        apiController.FindParameter("namespace")!.DataType.ShouldBe(TemplateParameterDataType.Unknown);
        apiController.FindParameter("language")!.DataType.ShouldBe(TemplateParameterDataType.Choice);
        apiController.FindParameter("name")!.DataType.ShouldBe(TemplateParameterDataType.String);
    }

    /// <summary>The bool parameter the Phase 3 dialog renders as a checkbox - and the one place the
    /// cache carries a display name worth showing instead of the raw parameter name.</summary>
    [Fact]
    public void PrefersADisplayNameOverTheParameterName()
    {
        var actions = SampleCache.Template("Microsoft.AspNetCore.Mvc.ApiController.10.0").FindParameter("actions")!;

        actions.Label.ShouldBe("Add Read/Write Actions");
        actions.DefaultValue.ShouldBe("false");
    }

    [Fact]
    public void FallsBackToTheParameterNameWhenTheDisplayNameIsBlank() =>
        SampleCache.Template("Microsoft.Common.Library.CSharp.10.0").FindParameter("Framework")!
            .Label.ShouldBe("Framework");

    [Fact]
    public void MarksTheNameParameter() =>
        SampleCache.Template("DotNet.Common.Class.CSharp").Parameters
            .Single(p => p.IsName).Name.ShouldBe("name");

    /// <summary>The distinction the whole namespace workaround hangs off - see NamespaceResolver.</summary>
    [Fact]
    public void KnowsWhichTemplatesTakeAnExplicitNamespace()
    {
        SampleCache.Template("Microsoft.AspNetCore.Mvc.ApiController.10.0").DeclaresNamespaceParameter.ShouldBeTrue();
        SampleCache.Template("DotNet.Common.Class.CSharp").DeclaresNamespaceParameter.ShouldBeFalse();
    }

    [Fact]
    public void FrameworkChoicesAreEmptyForATemplateWithNoFrameworkParameter() =>
        SampleCache.Template("DotNet.Common.Class.CSharp").FrameworkChoices.ShouldBeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    public void UnusableJsonReadsAsNull(string json) =>
        TemplateCacheReader.Read(json).ShouldBeNull();

    [Fact]
    public void AVersionThisReaderDoesNotUnderstandReadsAsNull() =>
        TemplateCacheReader.Read("""{"Version":"2.0.0.0","TemplateInfo":[]}""").ShouldBeNull();

    [Fact]
    public void AMissingVersionReadsAsNull() =>
        TemplateCacheReader.Read("""{"TemplateInfo":[]}""").ShouldBeNull();

    /// <summary>The engine bumps the trailing components for additive changes; refusing those would
    /// disable templates over a field this reader never looks at.</summary>
    [Theory]
    [InlineData("1.0.0.7")]
    [InlineData("1.0.0.9")]
    [InlineData("1.4")]
    public void ASupportedMajorVersionIsAccepted(string version) =>
        TemplateCacheReader.Read($$"""{"Version":"{{version}}","TemplateInfo":[]}""").ShouldNotBeNull();

    [Fact]
    public void AMissingTemplateListReadsAsNull() =>
        TemplateCacheReader.Read("""{"Version":"1.0.0.7"}""").ShouldBeNull();

    /// <summary>Distinct from unusable: the cache was read and simply holds nothing, which deserves a
    /// different message than "couldn't read the template cache".</summary>
    [Fact]
    public void AnEmptyTemplateListReadsAsAnEmptyList() =>
        TemplateCacheReader.Read("""{"Version":"1.0.0.7","TemplateInfo":[]}""").ShouldBeEmpty();

    [Fact]
    public void EntriesWithoutAnIdentityOrNameAreSkippedWithoutLosingTheRest()
    {
        const string Json = """
            {
              "Version": "1.0.0.7",
              "TemplateInfo": [
                { "Name": "No identity" },
                { "Identity": "No.Name" },
                "not even an object",
                { "Identity": "Good.One", "Name": "Good", "ShortNameList": ["good"] }
              ]
            }
            """;

        var templates = TemplateCacheReader.Read(Json);

        templates.ShouldNotBeNull();
        templates.Select(t => t.Identity).ShouldBe(["Good.One"]);
    }

    [Fact]
    public void AMissingFileReadsAsNull() =>
        TemplateCacheReader.ReadFile(Path.Combine(Path.GetTempPath(), $"no-such-cache-{Guid.NewGuid()}.json"))
            .ShouldBeNull();

    [Fact]
    public void ReadsTheSampleFromDisk() =>
        TemplateCacheReader.ReadFile(SampleCache.Path).ShouldNotBeEmpty();
}
