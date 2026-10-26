namespace IdePlusPlus.Shell;

/// <summary>The answer when an Ultra run is about to carry on from a team branch that a later run
/// already carried on from: take the later branch instead, keep the one chosen, or don't start.</summary>
public enum UltraStartChoice
{
    Cancel,
    StartFromLater,
    StartFromChosen,
}
