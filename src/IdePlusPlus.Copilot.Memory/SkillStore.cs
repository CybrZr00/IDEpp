namespace IdePlusPlus.Copilot.Memory;

public sealed record SkillSummary(string Name, string Description);

/// <summary>
/// Saved, reusable instructions the agent writes for itself - "how we handle X in this codebase" -
/// so a multi-step procedure explained once doesn't need re-explaining every session. Written
/// through the same propose_edit/diff/accept pipeline as any other file, the same way
/// project-memory.md is (see ProjectMemoryStore's own note); this class only ever reads.
///
/// Each skill is one markdown file under .ideplusplus/copilot/skills/, identified by its filename
/// (no extension) - not a name field inside the file, so there is exactly one place identity comes
/// from and read_skill's argument always matches a real path. It starts with a small frontmatter
/// block carrying just a description, cheap enough to list every skill's identity every turn
/// without also injecting every skill's full body:
///
///   ---
///   description: one line, shown in the per-turn catalog so the model knows when to read this one
///   ---
///   (the actual instructions, in markdown)
/// </summary>
public sealed class SkillStore
{
    private const string FrontmatterDelimiter = "---";

    public static string SkillsDirectory(string solutionDirectory) =>
        Path.Combine(ProjectMemoryStore.MemoryDirectory(solutionDirectory), "skills");

    /// <summary>Every skill's name and description - the full body is only read on demand via
    /// ReadSkill, once the model has decided a particular one is relevant.</summary>
    public IReadOnlyList<SkillSummary> ListSkills(string solutionDirectory)
    {
        var directory = SkillsDirectory(solutionDirectory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.md")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(file => new SkillSummary(Path.GetFileNameWithoutExtension(file), ExtractDescription(ReadAllTextSafely(file))))
            .ToList();
    }

    /// <param name="name">The skill's name exactly as ListSkills reported it - not a path.
    /// Sanitized before touching disk, since this comes from a tool call the model makes.</param>
    public string? ReadSkill(string solutionDirectory, string name)
    {
        if (!IsValidName(name))
        {
            return null;
        }

        var directory = Path.GetFullPath(SkillsDirectory(solutionDirectory));
        var fullPath = Path.GetFullPath(Path.Combine(directory, $"{name}.md"));

        // Belt and braces alongside IsValidName's character allowlist - confirms the resolved path
        // is still a direct child of the skills directory before it's ever opened.
        var directoryWithSeparator = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string ReadAllTextSafely(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>Hand-rolled rather than a YAML dependency for one field - a file with no
    /// frontmatter, or an empty description, still lists rather than silently disappearing from
    /// the catalog, since a skill written or edited by hand may not follow the format exactly.</summary>
    private static string ExtractDescription(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != FrontmatterDelimiter)
        {
            return "(no description)";
        }

        for (var i = 1; i < lines.Length && lines[i].Trim() != FrontmatterDelimiter; i++)
        {
            var colonIndex = lines[i].IndexOf(':');
            if (colonIndex <= 0 || !lines[i][..colonIndex].Trim().Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i][(colonIndex + 1)..].Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "(no description)";
    }
}
