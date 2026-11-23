namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// Checks the designer's contract commit against its design. The contract is the interface stubs every
/// owner builds against, committed first; the design says which files those are (<see cref="DesignFile.IsStub"/>),
/// and the commit may change those and nothing else. The designer has edit rights over the whole solution, so
/// this is what keeps "write the contract" from becoming "implement it" - or from quietly rewriting a file
/// that someone else will own.
///
/// Pure: it is given the paths the commit changed, so what it decides is checked without a repository.
/// </summary>
public static class ContractValidator
{
    /// <summary>
    /// <paramref name="changedInSolution"/> are the files the commit changed, as the design spells paths
    /// (relative to the solution folder, forward slashes); <paramref name="changedOutsideSolution"/> are any
    /// it changed that are not under the solution folder, which no design file can name. A commit that
    /// changes nothing is fine for a design with no stubs - the owners then start from the code as it is.
    /// Every refusal says which file and what to do about it.
    /// </summary>
    public static PipelineCheck<DesignPlan> Validate(
        DesignPlan plan, IReadOnlyList<string> changedInSolution, IReadOnlyList<string> changedOutsideSolution)
    {
        var errors = new ErrorList();
        var stubs = plan.Files.Where(f => f.IsStub).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = changedInSolution.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in changedInSolution.Where(p => !stubs.Contains(p)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var listed = plan.Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            errors.Add(listed
                ? $"Your commit changes {path}, which the design lists but does not declare as an interface stub. The contract "
                  + "commit may change only stubs: revert it - its owner makes that change - or set isStub on it if it is part of the contract."
                : $"Your commit changes {path}, which the design does not list. The contract commit may change only the files the "
                  + "design declares as interface stubs: revert it, or list it with an owner and isStub if it is part of the contract.");
        }

        foreach (var path in changedOutsideSolution.Order(StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Your commit changes {path}, which is outside the solution folder, so no design file can name it. Revert it.");
        }

        foreach (var stub in stubs.Where(s => !changed.Contains(s)).Order(StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"The design declares {stub} as an interface stub, but your commit does not change it. Write and commit "
                       + "the stub, or take isStub off the file.");
        }

        return errors.ResultOr(() => plan);
    }
}
