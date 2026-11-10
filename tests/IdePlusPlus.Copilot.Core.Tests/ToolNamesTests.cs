using System.Text.RegularExpressions;
using IdePlusPlus.Copilot.Core;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class ToolNamesTests
{
    private static readonly Regex ProviderPattern = new("^[a-zA-Z0-9_-]+$");

    [Theory]
    [InlineData("playwright__browser_navigate", "playwright__browser_navigate")]
    [InlineData("plugin:data:amplitude__run.query", "plugin_data_amplitude__run_query")]
    [InlineData("my server__list files", "my_server__list_files")]
    [InlineData("srv__café", "srv__caf_")]
    public void Sanitize_ReplacesCharactersTheProviderRejects(string input, string expected)
    {
        var result = ToolNames.Sanitize(input);

        result.ShouldBe(expected);
        ProviderPattern.IsMatch(result).ShouldBeTrue();
    }

    [Fact]
    public void Sanitize_CapsLengthAtSixtyFour()
    {
        var result = ToolNames.Sanitize(new string('a', 200));

        result.Length.ShouldBe(ToolNames.MaxLength);
    }

    [Fact]
    public void Sanitize_EmptyInput_StillProducesAValidName()
    {
        ProviderPattern.IsMatch(ToolNames.Sanitize("")).ShouldBeTrue();
    }

    [Fact]
    public void Sanitize_IsDeterministic()
    {
        ToolNames.Sanitize("a.b:c").ShouldBe(ToolNames.Sanitize("a.b:c"));
    }
}
