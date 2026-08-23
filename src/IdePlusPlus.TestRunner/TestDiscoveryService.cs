using System.Diagnostics;

namespace IdePlusPlus.TestRunner;

/// <summary>
/// Runs `dotnet test --list-tests` and parses its console output into TestCase records.
/// Isolated in its own file (like BuildDiagnosticParser) because this text format is VSTest-internal
/// and undocumented - it has shifted across SDK versions before, so a future break should be a
/// one-file fix.
/// </summary>
public sealed class TestDiscoveryService
{
    public async Task<IReadOnlyList<TestCase>> DiscoverAsync(string testProjectPath, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(testProjectPath),
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(testProjectPath);
        startInfo.ArgumentList.Add("--list-tests");
        startInfo.ArgumentList.Add("--nologo");

        var lines = new List<string>();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lines.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lines.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return ParseListTestsOutput(lines, testProjectPath);
    }

    /// <summary>
    /// Real observed shape (SDK 10.0.301, xunit 2.9.3):
    /// <code>
    /// Test run for &lt;dll path&gt; (.NETCoreApp,Version=v10.0)
    /// The following Tests are available:
    ///     Namespace.ClassName.MethodName
    ///     Namespace.ClassName.MethodName
    /// </code>
    /// Every fully-qualified-name line is indented 4 spaces under the "following Tests are
    /// available" header line. Everything before that header (restore/build noise) is ignored.
    /// </summary>
    private static List<TestCase> ParseListTestsOutput(List<string> lines, string testProjectPath)
    {
        var results = new List<TestCase>();
        var headerIndex = lines.FindIndex(l => l.Contains("following Tests are available", StringComparison.OrdinalIgnoreCase));
        if (headerIndex < 0)
        {
            return results;
        }

        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var fullyQualifiedName = lines[i].Trim();
            if (fullyQualifiedName.Length == 0)
            {
                continue;
            }

            // Best-effort split on the last '.' to separate class from method - tolerates
            // namespaces/nested classes fine since only the final segment is treated as the
            // method name. Generic method names (e.g. "Method<T>") or parameterized NUnit-style
            // names aren't specially unpacked; this is inherently heuristic.
            var lastDot = fullyQualifiedName.LastIndexOf('.');
            var className = lastDot > 0 ? fullyQualifiedName[..lastDot] : fullyQualifiedName;
            var methodName = lastDot > 0 && lastDot + 1 < fullyQualifiedName.Length
                ? fullyQualifiedName[(lastDot + 1)..]
                : fullyQualifiedName;

            results.Add(new TestCase(testProjectPath, fullyQualifiedName, className, methodName));
        }

        return results;
    }
}
