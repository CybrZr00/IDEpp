using System.Text;

namespace IdePlusPlus.Copilot.Core.Team;

/// <summary>
/// The [team notices] block appended to a worker's tool results - how a direct message, or a
/// peer's board entry that should change what this worker is doing, reaches them mid-step rather
/// than at the start of their next cycle. Agensh delivers these "at the end of each tool call", and
/// so does this: a tool result is the one place new text can enter a model's context without
/// breaking the shape of the conversation.
/// </summary>
public static class TeamNotices
{
    public const string Header = "[team notices]";

    /// <summary>More than this in one block buries the result it is attached to; the rest stay on
    /// the board for the worker's next board_read.</summary>
    public const int MaxBoardNotices = 10;

    /// <summary>FAIL (stop that approach), CLAIM (possible collision) and PATCH_SUMMARY (something
    /// was merged, so sync before building on stale code). OBSERVED and FACT can wait.</summary>
    public static readonly IReadOnlySet<TeamBoardEntryType> InterruptingTypes = new HashSet<TeamBoardEntryType>
    {
        TeamBoardEntryType.Fail,
        TeamBoardEntryType.Claim,
        TeamBoardEntryType.PatchSummary,
    };

    /// <summary>Null when there is nothing to deliver, so the caller can leave the result alone.</summary>
    public static string? Compose(string worker, TeamBoard board, TeamMessageBus bus)
    {
        var direct = bus.TakeDirect(worker);
        var entries = board.TakeUnseen(worker, InterruptingTypes);
        if (direct.Count == 0 && entries.Count == 0)
        {
            return null;
        }

        var block = new StringBuilder(Header);
        foreach (var message in direct)
        {
            block.Append($"\nDM from {message.From}: {message.Text}");
        }

        foreach (var entry in entries.TakeLast(MaxBoardNotices))
        {
            block.Append("\nBoard: ").Append(TeamBoard.Format(entry));
        }

        if (entries.Count > MaxBoardNotices)
        {
            block.Append($"\n({entries.Count - MaxBoardNotices} older board entries not shown - board_read has them)");
        }

        if (entries.Count > 0)
        {
            block.Append(
                "\n(A FAIL means stop that approach. A CLAIM on what you are doing is a collision - settle it "
                + "with team_dm now. A PATCH_SUMMARY means the team branch moved - team_sync before you merge.)");
        }

        return block.ToString();
    }
}
