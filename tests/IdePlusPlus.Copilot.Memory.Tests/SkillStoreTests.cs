using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Memory.Tests;

public sealed class SkillStoreTests : IDisposable
{
    private readonly string _solutionDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-skills-{Guid.NewGuid()}");
    private readonly SkillStore _store = new();

    public SkillStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    private void WriteSkill(string name, string content)
    {
        Directory.CreateDirectory(SkillStore.SkillsDirectory(_solutionDirectory));
        File.WriteAllText(Path.Combine(SkillStore.SkillsDirectory(_solutionDirectory), $"{name}.md"), content);
    }

    [Fact]
    public void SkillsDirectory_IsUnderTheCopilotSubdirectoryOfDotIdePlusPlus()
    {
        var path = SkillStore.SkillsDirectory(_solutionDirectory);

        path.ShouldBe(Path.Combine(_solutionDirectory, ".ideplusplus", "copilot", "skills"));
    }

    [Fact]
    public void ListSkills_NoDirectoryYet_ReturnsEmpty()
    {
        _store.ListSkills(_solutionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void ListSkills_UsesTheFilenameAsTheNameAndTheFrontmatterAsTheDescription()
    {
        WriteSkill("deploy-checklist", "---\ndescription: Steps to cut a release\n---\n\nDo the thing.");

        var skills = _store.ListSkills(_solutionDirectory);

        skills.ShouldHaveSingleItem();
        skills[0].Name.ShouldBe("deploy-checklist");
        skills[0].Description.ShouldBe("Steps to cut a release");
    }

    /// <summary>A skill written or edited by hand may not follow the frontmatter format exactly - it
    /// should still show up in the catalog rather than silently vanishing.</summary>
    [Fact]
    public void ListSkills_WithNoFrontmatter_StillListsWithAPlaceholderDescription()
    {
        WriteSkill("freehand", "Just some notes, no frontmatter at all.");

        var skills = _store.ListSkills(_solutionDirectory);

        skills.ShouldHaveSingleItem();
        skills[0].Name.ShouldBe("freehand");
        skills[0].Description.ShouldBe("(no description)");
    }

    [Fact]
    public void ListSkills_WithAnEmptyDescriptionField_FallsBackToThePlaceholder()
    {
        WriteSkill("blank-description", "---\ndescription: \n---\n\nBody.");

        _store.ListSkills(_solutionDirectory)[0].Description.ShouldBe("(no description)");
    }

    [Fact]
    public void ListSkills_ReturnsEveryMarkdownFileInTheDirectory()
    {
        WriteSkill("alpha", "---\ndescription: A\n---\nBody");
        WriteSkill("beta", "---\ndescription: B\n---\nBody");

        var skills = _store.ListSkills(_solutionDirectory);

        skills.Select(s => s.Name).ShouldBe(["alpha", "beta"], ignoreOrder: true);
    }

    [Fact]
    public void ReadSkill_ForAnExistingSkill_ReturnsTheFullFileContent()
    {
        WriteSkill("deploy-checklist", "---\ndescription: Steps\n---\n\nDo the thing.");

        _store.ReadSkill(_solutionDirectory, "deploy-checklist")
            .ShouldBe("---\ndescription: Steps\n---\n\nDo the thing.");
    }

    [Fact]
    public void ReadSkill_ForAMissingSkill_ReturnsNull()
    {
        _store.ReadSkill(_solutionDirectory, "does-not-exist").ShouldBeNull();
    }

    /// <summary>The name comes from a tool call the model makes, so it has to be treated as
    /// untrusted input - a name shaped like a path must never escape the skills directory.</summary>
    [Theory]
    [InlineData("../secrets")]
    [InlineData("..\\secrets")]
    [InlineData("sub/dir")]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadSkill_ForANameShapedLikeAPathOrEmpty_ReturnsNull(string name)
    {
        _store.ReadSkill(_solutionDirectory, name).ShouldBeNull();
    }

    [Fact]
    public void ReadSkill_ForAnAbsolutePathAsTheName_ReturnsNull()
    {
        var secretPath = Path.Combine(Path.GetTempPath(), "outside-secret.txt");
        File.WriteAllText(secretPath, "top secret");

        try
        {
            _store.ReadSkill(_solutionDirectory, secretPath).ShouldBeNull();
        }
        finally
        {
            File.Delete(secretPath);
        }
    }
}
