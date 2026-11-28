using System.Text;

namespace IdePlusPlus.ProjectSystem.Tests;

/// <summary>One scratch project created (and listed in the solution file) by <see cref="TempSolutionFixture"/>.</summary>
/// <param name="Name">Project name, which is also its directory name.</param>
/// <param name="ProjectPath">Absolute path of the .csproj.</param>
/// <param name="SolutionRelativePath">The path exactly as written into the solution file - 'App\App.csproj'.</param>
public sealed record TempProject(string Name, string ProjectPath, string SolutionRelativePath);

/// <summary>
/// A throwaway solution on disk: a Guid-named directory under <see cref="Path.GetTempPath()"/> holding
/// N real, SDK-style projects plus a hand-written .sln with the real
/// "Microsoft Visual Studio Solution File, Format Version 12.00" header, so
/// Microsoft.Build.Construction.SolutionFile.Parse (and therefore SolutionLoader/SolutionBuildTarget)
/// accepts it - the tests that use this fixture fail loudly if it ever stops being parseable.
///
/// These are deliberately hand-written rather than `dotnet new` + `dotnet sln add`: the tests need
/// exact control over a project's contents (a deliberately broken .csproj, an extra Folder item) and
/// no restore, which is what makes them run in a second rather than a minute.
/// </summary>
public sealed class TempSolutionFixture : IDisposable
{
    public const string SlnFormatVersionHeader = "Microsoft Visual Studio Solution File, Format Version 12.00";

    private readonly List<TempProject> _projects = [];
    private bool _disposed;

    public TempSolutionFixture(params string[] projectNames)
    {
        Directory = Path.Combine(Path.GetTempPath(), $"ideplusplus-solution-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);

        SolutionName = "Scratch";
        SolutionPath = Path.Combine(Directory, $"{SolutionName}.sln");

        foreach (var projectName in projectNames.Length == 0 ? ["App"] : projectNames)
        {
            _projects.Add(CreateProject(projectName));
        }

        File.WriteAllText(SolutionPath, BuildSolutionFile());
    }

    public string Directory { get; }

    public string SolutionName { get; }

    public string SolutionPath { get; }

    public IReadOnlyList<TempProject> Projects => _projects;

    public TempProject Project(string name) =>
        _projects.Single(p => p.Name == name);

    /// <summary>Overwrites a project file - the broken-project cases. Pass e.g. '&lt;Project&gt;&lt;broken'.
    /// No solution rewrite is needed: the project entry is already in the .sln.</summary>
    public void WriteProjectFile(string projectName, string contents) =>
        File.WriteAllText(Project(projectName).ProjectPath, contents);

    /// <summary>Writes the same project list as a real .slnx beside the .sln, and returns its path.</summary>
    public string WriteSolutionX()
    {
        var builder = new StringBuilder();
        builder.AppendLine("<Solution>");
        foreach (var project in _projects)
        {
            builder.AppendLine($"""  <Project Path="{project.SolutionRelativePath}" />""");
        }

        builder.AppendLine("</Solution>");

        var solutionXPath = Path.Combine(Directory, $"{SolutionName}.slnx");
        File.WriteAllText(solutionXPath, builder.ToString());
        return solutionXPath;
    }

    private TempProject CreateProject(string name)
    {
        var projectDirectory = System.IO.Directory.CreateDirectory(Path.Combine(Directory, name)).FullName;
        var projectPath = Path.Combine(projectDirectory, $"{name}.csproj");

        File.WriteAllText(projectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>{name}</RootNamespace>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(projectDirectory, "Class1.cs"), $"namespace {name}; public class Class1 {{ }}");

        return new TempProject(name, projectPath, $@"{name}\{name}.csproj");
    }

    private string BuildSolutionFile()
    {
        var builder = new StringBuilder();
        builder.AppendLine(SlnFormatVersionHeader);
        builder.AppendLine("# Visual Studio Version 17");
        builder.AppendLine("VisualStudioVersion = 17.0.31903.59");
        builder.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");

        foreach (var project in _projects)
        {
            // Same project-type GUID `dotnet new sln` uses for a C# project.
            var projectGuid = ProjectGuid(project.Name);
            // A plain interpolated string: "{{" is one literal brace, so "{{{projectGuid}}}" is the GUID in braces.
            builder.AppendLine(
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{project.Name}\", \"{project.SolutionRelativePath}\", \"{{{projectGuid}}}\"");
            builder.AppendLine("EndProject");
        }

        builder.AppendLine("Global");
        builder.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        builder.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        builder.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var project in _projects)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                builder.AppendLine($"\t\t{{{ProjectGuid(project.Name)}}}.{configuration}|Any CPU.ActiveCfg = {configuration}|Any CPU");
                builder.AppendLine($"\t\t{{{ProjectGuid(project.Name)}}}.{configuration}|Any CPU.Build.0 = {configuration}|Any CPU");
            }
        }

        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        builder.AppendLine("\t\tHideSolutionNode = FALSE");
        builder.AppendLine("\tEndGlobalSection");
        builder.AppendLine("EndGlobal");

        return builder.ToString();
    }

    /// <summary>Stable per project name within this fixture, so the .sln/.slnx agree and the file can be
    /// rewritten without changing identities.</summary>
    private static Guid ProjectGuid(string projectName)
    {
        var bytes = new byte[16];
        var nameBytes = Encoding.UTF8.GetBytes(projectName);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = i < nameBytes.Length ? nameBytes[i] : (byte)i;
        }

        bytes[7] = 0x4B;
        return new Guid(bytes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(Directory, "*", SearchOption.AllDirectories))
            {
                // A read-only test (ProjectFolderWriter's) deliberately sets the attribute.
                File.SetAttributes(path, FileAttributes.Normal);
            }

            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a lingering handle shouldn't fail an unrelated test.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale.
        }
    }
}
