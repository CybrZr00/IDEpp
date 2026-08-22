namespace IdePlusPlus.SourceControl;

/// <summary>Parses `git branch --format=%(refname:short)&lt;TAB&gt;%(HEAD)` output.</summary>
public static class GitBranchParser
{
    public static IReadOnlyList<GitBranch> Parse(string output)
    {
        var branches = new List<GitBranch>();

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            branches.Add(new GitBranch(parts[0], parts[1].Trim() == "*"));
        }

        return branches;
    }
}
