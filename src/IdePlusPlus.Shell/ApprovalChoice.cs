namespace IdePlusPlus.Shell;

/// <summary>The answer to an approval prompt that can also be answered for everything still to
/// come - an Ultra team's run_command, where several workers asking one after another would
/// otherwise keep the user at the keyboard for the whole run.</summary>
public enum ApprovalChoice
{
    Deny,
    Allow,
    AllowAll,
}
