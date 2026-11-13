using System.Diagnostics;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

/// <summary>
/// MSBuildLocator points this process at one SDK through environment variables, and every child
/// `dotnet` inherits them. Where plain `dotnet` resolves to a different SDK (a preview installed beside
/// the stable one, or a global.json pin) the child runs one SDK's MSBuild against another's targets and
/// dies with a NuGet.Frameworks FileLoadException. Worked on the start info, never on this process's
/// own environment, which the in-process loader still needs.
/// </summary>
public sealed class MSBuildLocatorEnvironmentTests
{
    [Fact]
    public void RemoveFrom_TakesTheLocatorsVariablesOutOfTheChildsEnvironment_AndKeepsTheRest()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.Environment["MSBUILD_EXE_PATH"] = @"C:\sdk\10.0.403\MSBuild.dll";
        startInfo.Environment["MSBuildSDKsPath"] = @"C:\sdk\10.0.403\Sdks";
        startInfo.Environment["MSBuildExtensionsPath"] = @"C:\sdk\10.0.403";
        startInfo.Environment["MSBuildLoadMicrosoftTargetsReadOnly"] = "true";
        startInfo.Environment["DOTNET_ROOT"] = @"C:\Program Files\dotnet";
        startInfo.Environment["SOMETHING_ELSE"] = "kept";

        MSBuildLocatorEnvironment.RemoveFrom(startInfo);

        foreach (var name in MSBuildLocatorEnvironment.Variables)
        {
            startInfo.Environment.ContainsKey(name).ShouldBeFalse($"{name} would be inherited by the child");
        }

        startInfo.Environment["DOTNET_ROOT"].ShouldBe(@"C:\Program Files\dotnet");
        startInfo.Environment["SOMETHING_ELSE"].ShouldBe("kept");
    }

    [Fact]
    public void RemoveFrom_IsCaseInsensitive_AsWindowsEnvironmentVariablesAre()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.Environment["msbuildsdkspath"] = @"C:\sdk\Sdks";

        MSBuildLocatorEnvironment.RemoveFrom(startInfo);

        startInfo.Environment.Keys.ShouldNotContain(key => key.Equals("MSBuildSDKsPath", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoveFrom_NeverTouchesThisProcessesOwnEnvironment()
    {
        // The in-process loader reads these every time a solution is evaluated.
        const string name = "MSBuildSDKsPath";
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, @"C:\sdk\Sdks");
        try
        {
            MSBuildLocatorEnvironment.RemoveFrom(new ProcessStartInfo("dotnet"));

            Environment.GetEnvironmentVariable(name).ShouldBe(@"C:\sdk\Sdks");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
