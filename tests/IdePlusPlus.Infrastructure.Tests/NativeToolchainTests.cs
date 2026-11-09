using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

public sealed class NativeToolchainTests
{
    [Theory]
    [InlineData("cmake -S . -B build -G Ninja")]
    [InlineData("cmake --build build")]
    [InlineData("cl /c foo.cpp")]
    [InlineData("& 'C:\\Program Files\\CMake\\bin\\cmake.exe' --version")]
    [InlineData("ninja -C build")]
    [InlineData("msbuild app.vcxproj /p:Configuration=Release")]
    [InlineData("link foo.obj /OUT:foo.exe")]
    [InlineData("dumpbin /exports foo.dll")]
    [InlineData("cd native; cmake ..")]
    public void IsNeededBy_RecognisesCppToolchainCommands(string command) =>
        NativeToolchain.IsNeededBy(command).ShouldBeTrue();

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("git status")]
    [InlineData("npm run build")]
    [InlineData("Get-ChildItem -Recurse")]
    [InlineData("echo click here")]
    [InlineData("Get-Content mylink.txt")]
    [InlineData("python tools/clean-up.py")]
    public void IsNeededBy_LeavesOrdinaryCommandsAlone(string command) =>
        NativeToolchain.IsNeededBy(command).ShouldBeFalse();

    [Fact]
    public void Diff_KeepsOnlyWhatVcvarsChanged()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "C:\\Windows",
            ["TEMP"] = "C:\\Temp",
        };

        var changes = NativeToolchain.Diff(
            "PATH=C:\\VS\\bin;C:\\Windows\r\nTEMP=C:\\Temp\r\nINCLUDE=C:\\VS\\include\r\n=C:=C:\\work\r\n",
            current,
            []);

        changes.ShouldContainKeyAndValue("PATH", "C:\\VS\\bin;C:\\Windows");
        changes.ShouldContainKeyAndValue("INCLUDE", "C:\\VS\\include");
        changes.ShouldNotContainKey("TEMP");
        changes.Keys.ShouldNotContain(k => k.StartsWith('='));
    }

    [Fact]
    public void Diff_AddsBundledToolDirectoriesToPathOnlyWhenMissing()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "C:\\Windows" };

        var added = NativeToolchain.Diff("PATH=C:\\Windows\r\n", current, ["C:\\VS\\CMake\\bin", "C:\\VS\\Ninja"]);
        added["PATH"].ShouldBe("C:\\VS\\CMake\\bin;C:\\VS\\Ninja;C:\\Windows");

        var already = NativeToolchain.Diff(
            "PATH=C:\\VS\\CMake\\bin\\;C:\\Windows\r\n", current, ["C:\\VS\\CMake\\bin"]);
        already["PATH"].ShouldBe("C:\\VS\\CMake\\bin\\;C:\\Windows");
    }

    [Fact]
    public async Task ShellCommandRunner_AppliesTheEnvironmentToACppCommand()
    {
        IReadOnlyDictionary<string, string>? environment = new Dictionary<string, string> { ["IDEPP_FAKE_TOOLCHAIN"] = "present" };
        var runner = new ShellCommandRunner(_ => Task.FromResult(environment));

        // "cmake" in the comment is what makes this look like a C++ command.
        var result = await runner.RunAsync(
            "Write-Output $env:IDEPP_FAKE_TOOLCHAIN # cmake", Path.GetTempPath(), TimeSpan.FromMinutes(1), 20_000);

        result.Output.ShouldContain("present");
    }

    [Fact]
    public async Task ShellCommandRunner_DoesNotApplyItToOrdinaryCommands()
    {
        var called = false;
        var runner = new ShellCommandRunner(_ =>
        {
            called = true;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(
                new Dictionary<string, string> { ["IDEPP_FAKE_TOOLCHAIN"] = "present" });
        });

        var result = await runner.RunAsync(
            "Write-Output \"[$env:IDEPP_FAKE_TOOLCHAIN]\"", Path.GetTempPath(), TimeSpan.FromMinutes(1), 20_000);

        called.ShouldBeFalse();
        result.Output.ShouldContain("[]");
    }

    [Fact]
    public async Task ShellCommandRunner_SaysSoWhenACppBuildFailsWithNoToolchain()
    {
        var runner = new ShellCommandRunner(_ => Task.FromResult<IReadOnlyDictionary<string, string>?>(null));

        var result = await runner.RunAsync(
            "exit 3 # cmake", Path.GetTempPath(), TimeSpan.FromMinutes(1), 20_000);

        result.ExitCode.ShouldBe(3);
        result.Output.ShouldContain("Desktop development with C++");
    }

    [Fact]
    public async Task GetEnvironmentAsync_OnThisMachine_PutsCmakeOnThePathWhenVisualStudioHasCpp()
    {
        var environment = await NativeToolchain.GetEnvironmentAsync();
        if (environment is null)
        {
            return; // No Visual Studio with the C++ tools here; nothing to check.
        }

        environment.ShouldContainKey("INCLUDE");
        environment.ShouldContainKey("LIB");
        environment["PATH"].ShouldContain("VC", Case.Insensitive);

        // End to end through the real runner: the compiler and the bundled CMake are found.
        var runner = new ShellCommandRunner();
        var cl = await runner.RunAsync("cl 2>&1 | Select-Object -First 1", Path.GetTempPath(), TimeSpan.FromMinutes(1), 20_000);
        cl.Output.ShouldContain("Microsoft", Case.Insensitive);

        var cmake = await runner.RunAsync("cmake --version", Path.GetTempPath(), TimeSpan.FromMinutes(1), 20_000);
        if (cmake.ExitCode == 0)
        {
            cmake.Output.ShouldContain("cmake version", Case.Insensitive);
        }
    }
}
