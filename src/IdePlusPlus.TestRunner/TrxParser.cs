using System.Xml.Linq;

namespace IdePlusPlus.TestRunner;

/// <summary>
/// Parses a VSTest .trx result file into TestResult records. Isolated in its own file (like
/// BuildDiagnosticParser) because TRX's schema is VSTest-internal and undocumented - it has shifted
/// across SDK releases before, so a future break should be a one-file fix. Every element/attribute
/// access is defensive: a missing or renamed element degrades that one result to a best-effort
/// partial record (or is skipped) rather than throwing and aborting the whole file.
/// </summary>
public static class TrxParser
{
    // Real observed namespace (SDK 10.0.301 / VSTest): xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TestResult> Parse(string trxFilePath)
    {
        var results = new List<TestResult>();

        XDocument document;
        try
        {
            document = XDocument.Load(trxFilePath);
        }
        catch
        {
            return results;
        }

        var root = document.Root;
        if (root is null)
        {
            return results;
        }

        // TestDefinitions/UnitTest gives id -> className/name, which is a slightly more reliable
        // source of a structured FQN than UnitTestResult's own free-text "testName" attribute.
        var idToFullyQualifiedName = new Dictionary<string, string>();
        foreach (var unitTest in root.Descendants(Ns + "UnitTest"))
        {
            var id = (string?)unitTest.Attribute("id");
            if (id is null)
            {
                continue;
            }

            var testMethod = unitTest.Element(Ns + "TestMethod");
            var className = (string?)testMethod?.Attribute("className");
            var methodName = (string?)testMethod?.Attribute("name");
            if (className is not null && methodName is not null)
            {
                idToFullyQualifiedName[id] = $"{className}.{methodName}";
            }
        }

        foreach (var unitTestResult in root.Descendants(Ns + "UnitTestResult"))
        {
            try
            {
                results.Add(ParseResult(unitTestResult, idToFullyQualifiedName));
            }
            catch
            {
                // One malformed/unexpected result entry shouldn't abort parsing the rest of the file.
            }
        }

        return results;
    }

    private static TestResult ParseResult(XElement unitTestResult, IReadOnlyDictionary<string, string> idToFullyQualifiedName)
    {
        var testId = (string?)unitTestResult.Attribute("testId");
        var testName = (string?)unitTestResult.Attribute("testName");
        var fullyQualifiedName = (testId is not null && idToFullyQualifiedName.TryGetValue(testId, out var mapped))
            ? mapped
            : testName ?? "<unknown test>";

        var outcome = ParseOutcome((string?)unitTestResult.Attribute("outcome"));

        var duration = TimeSpan.Zero;
        var durationText = (string?)unitTestResult.Attribute("duration");
        if (durationText is not null)
        {
            TimeSpan.TryParse(durationText, out duration);
        }

        var errorInfo = unitTestResult.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
        var errorMessage = errorInfo?.Element(Ns + "Message")?.Value;
        var stackTrace = errorInfo?.Element(Ns + "StackTrace")?.Value;

        return new TestResult(fullyQualifiedName, outcome, duration, errorMessage, stackTrace);
    }

    private static TestOutcome ParseOutcome(string? outcome) => outcome switch
    {
        "Passed" => TestOutcome.Passed,
        "Failed" => TestOutcome.Failed,
        "NotExecuted" => TestOutcome.Skipped,
        _ => TestOutcome.NotRun,
    };
}
