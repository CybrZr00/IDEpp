using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team;

/// <summary>
/// What the Ultra Pipeline run hands each agent's <see cref="TeamToolDispatcher"/> so the dispatcher
/// can enforce the rules that only the run knows: who owns which file, and whether the agent has a
/// colleague's request to answer before it does anything else.
///
/// Everything here is optional on the dispatcher. A dispatcher built without hooks - the original
/// Ultra team, and every test written before the pipeline - behaves exactly as it always did, and one
/// whose role is <see cref="IdePlusPlus.Copilot.Core.Team.TeamRoleEditScope.OwnedFilesOnly"/> but has no
/// hooks may edit nothing, since a rule that cannot be checked is not a permission.
/// </summary>
public sealed class TeamPipelineHooks
{
    /// <param name="ownership">The ownership map as it stands now. A function rather than a map so the
    /// run can replace it when an owner claims a file the design did not list, and every dispatcher
    /// sees the new one on its next call.</param>
    /// <param name="toolGuard">Asked before edit_file, team_commit, team_merge and run_command with the
    /// tool's name: a reason to refuse it (a colleague's request is waiting for this agent's answer) or
    /// null. Null leaves those tools unguarded.</param>
    public TeamPipelineHooks(Func<OwnershipMap> ownership, Func<string, string?>? toolGuard = null)
    {
        Ownership = ownership;
        ToolGuard = toolGuard;
    }

    public Func<OwnershipMap> Ownership { get; }

    public Func<string, string?>? ToolGuard { get; }
}
