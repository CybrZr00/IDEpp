namespace IdePlusPlus.TestRunner;

/// <summary>One test discovered via `dotnet test --list-tests` for a given test project.</summary>
public sealed record TestCase(string ProjectPath, string FullyQualifiedName, string ClassName, string MethodName);
