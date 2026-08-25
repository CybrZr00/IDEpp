using System.Diagnostics;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

/// <summary>
/// Runs real PowerShell, for the same reason BuildServiceTests runs a real dotnet build: the
/// behaviour worth pinning here is process lifetime and output capture, neither of which a mocked
/// process has.
/// </summary>
public sealed class ShellCommandRunnerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ideplusplus-shell-{Guid.NewGuid():N}");

    private readonly ShellCommandRunner _runner = new();

    public ShellCommandRunnerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked child holding the directory would land here; not worth failing over.
        }
    }

    private Task<ShellCommandResult> RunAsync(
        string command, TimeSpan? timeout = null, int maxOutputChars = 20_000, CancellationToken ct = default) =>
        _runner.RunAsync(command, _directory, timeout ?? TimeSpan.FromMinutes(1), maxOutputChars, ct);

    [Fact]
    public async Task RunAsync_CapturesStandardOutputAndSucceeds()
    {
        var result = await RunAsync("Write-Output 'hello from powershell'");

        result.ExitCode.ShouldBe(0);
        result.TimedOut.ShouldBeFalse();
        result.Output.ShouldContain("hello from powershell");
    }

    [Fact]
    public async Task RunAsync_CapturesStandardErrorToo()
    {
        var result = await RunAsync("[Console]::Error.WriteLine('this went to stderr')");

        result.Output.ShouldContain("this went to stderr");
    }

    [Fact]
    public async Task RunAsync_ReportsANonZeroExitCode()
    {
        var result = await RunAsync("exit 3");

        result.ExitCode.ShouldBe(3);
        result.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_RunsInTheGivenWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_directory, "marker.txt"), "x");

        var result = await RunAsync("Get-ChildItem -Name");

        result.Output.ShouldContain("marker.txt");
    }

    [Fact]
    public async Task RunAsync_TruncatesRunawayOutput()
    {
        var result = await RunAsync("1..5000 | ForEach-Object { 'line ' + $_ }", maxOutputChars: 500);

        result.Output.ShouldContain("output truncated");
        result.Output.Length.ShouldBeLessThan(2_000);
    }

    /// <summary>
    /// The reason the tree is killed rather than just the shell: a real command spawns children,
    /// and killing only PowerShell would leave them running. Start-Sleep is the simplest stand-in.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenItExceedsTheTimeout_KillsTheProcessAndSaysSo()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await RunAsync("Start-Sleep -Seconds 60", timeout: TimeSpan.FromSeconds(3));

        stopwatch.Stop();
        result.TimedOut.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_ThrowsRatherThanReportingATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var task = RunAsync("Start-Sleep -Seconds 60", timeout: TimeSpan.FromMinutes(5), ct: cancellation.Token);

        await Task.Delay(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => task);
    }

    /// <summary>Non-interactive, so anything waiting for input fails instead of hanging forever on
    /// a prompt nobody can see.</summary>
    [Fact]
    public async Task RunAsync_ForACommandThatReadsInput_DoesNotHang()
    {
        var result = await RunAsync("$x = Read-Host 'name'; Write-Output \"got:$x\"", timeout: TimeSpan.FromSeconds(20));

        result.TimedOut.ShouldBeFalse();
    }
}
