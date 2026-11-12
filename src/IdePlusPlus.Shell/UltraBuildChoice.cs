namespace IdePlusPlus.Shell;

/// <summary>The answer when the solution does not build and an Ultra message was just sent: have the
/// copilot fix the build first (the message is kept for sending again afterwards), go ahead anyway,
/// or don't start.</summary>
public enum UltraBuildChoice
{
    Cancel,
    FixFirst,
    Continue,
}
