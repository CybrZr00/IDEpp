namespace IdePlusPlus.Shell.Team;

/// <summary>How a message sent at Ultra effort is carried out.</summary>
public enum UltraStrategy
{
    /// <summary>The Ultra team: workers sharing a backlog, each in a worktree of its own. What Ultra has always been, and the default.</summary>
    Team,

    /// <summary>The pipeline: surveyors read the code, a designer fixes the contract and who owns which file, those owners build, then
    /// judges check the result and the owners repair what they find, until the judges are satisfied or the loop's limit is reached.</summary>
    Pipeline,
}
