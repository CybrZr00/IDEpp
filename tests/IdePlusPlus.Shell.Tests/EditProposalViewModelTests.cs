using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Editor;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// Covers ToolResultText, which is what the model is told came of its own edit. Before it existed
/// an edit-only iteration put nothing in chat history but a synthetic "- Applied edit to {path}."
/// user message, so the model could not tell an applied edit from a refused one and had no way to
/// notice it had just removed most of a file.
/// </summary>
public class EditProposalViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusProposalTests", Guid.NewGuid().ToString("N"));

    private readonly EditApplicationService _service;

    public EditProposalViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _service = new EditApplicationService(
            new EditorWorkspace(Path.Combine(_root, "recent-files.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private EditProposalViewModel Complete(EditOperation operation, string? workspace, string argumentsJson = "{}")
    {
        var proposal = new EditProposalViewModel("call-1", _service);
        proposal.Complete(
            new EditProposalEvent
            {
                ToolCallId = "call-1",
                Status = EditProposalStatus.Complete,
                Operation = operation,
                ArgumentsJson = argumentsJson,
            },
            workspace);
        return proposal;
    }

    private static EditOperation ReplaceRange(string file, int? start, int? end, string newText) =>
        new(file, "replace_range", start, end, newText);

    [Fact]
    public void ToolResultText_BeforeTheCallEvenCompletes_SaysNothingWasWritten()
    {
        new EditProposalViewModel("call-1", _service).ToolResultText.ShouldContain("cut off");
    }

    [Fact]
    public void Complete_OutsideTheWorkspace_ReportsThatNothingWasApplied()
    {
        var proposal = Complete(ReplaceRange(@"..\..\Evil.cs", 1, 1, "x"), _root);

        proposal.IsDecided.ShouldBeTrue();
        proposal.WasApplied.ShouldBeFalse();
        proposal.ToolResultText.ShouldContain("Not applied");
        proposal.ToolResultText.ShouldContain("outside the open solution");
    }

    [Fact]
    public void Complete_WithNoSolutionOpen_SaysSo()
    {
        Complete(ReplaceRange("Foo.cs", 1, 1, "x"), null)
            .ToolResultText.ShouldContain("no solution is open");
    }

    /// <summary>The refusal message is the model's only route back to a working edit, so the
    /// actionable part of it has to survive into the tool result.</summary>
    [Fact]
    public void Complete_WithAStaleLineRange_PassesTheActionableReasonThrough()
    {
        WriteFile("Foo.cs", "a\nb\nc");

        var result = Complete(ReplaceRange("Foo.cs", 2, 99, "x"), _root).ToolResultText;

        result.ShouldContain("Not applied to Foo.cs");
        result.ShouldContain("99");
        result.ShouldContain("Re-read the file");
    }

    [Fact]
    public void Complete_WithoutAnEndLine_IsReportedAsNotApplied()
    {
        WriteFile("Foo.cs", "a\nb\nc");

        Complete(ReplaceRange("Foo.cs", 2, null, "x"), _root)
            .ToolResultText.ShouldContain("end_line");
    }

    [Fact]
    public void Complete_ButNotYetDecided_SaysItIsWaitingOnTheUser()
    {
        WriteFile("Foo.cs", "a\nb\nc");

        var proposal = Complete(ReplaceRange("Foo.cs", 2, 2, "B"), _root);

        proposal.IsDecided.ShouldBeFalse();
        proposal.ToolResultText.ShouldContain("Waiting for the user");
        proposal.ToolResultText.ShouldContain("Nothing has been written");
    }

    [Fact]
    public void Accept_ReportsTheLineCountsAndThatLineNumbersHaveMoved()
    {
        WriteFile("Foo.cs", "a\nb\nc\nd\ne");
        var proposal = Complete(ReplaceRange("Foo.cs", 2, 4, "B"), _root);

        proposal.AcceptCommand.Execute(null);

        proposal.WasApplied.ShouldBeTrue();
        proposal.ToolResultText.ShouldContain("Applied to Foo.cs");
        proposal.ToolResultText.ShouldContain("now 3 line(s) long (was 5)");
        proposal.ToolResultText.ShouldContain("line number");
        File.ReadAllText(Path.Combine(_root, "Foo.cs")).ShouldBe("a\nB\ne");
    }

    /// <summary>The tripwire for silent truncation: the model can only notice it deleted most of a
    /// file if the result says how much of it is left.</summary>
    [Fact]
    public void Accept_AfterALargeDeletion_ReportsTheShrinkInTheResult()
    {
        WriteFile("Big.cs", string.Join("\n", Enumerable.Range(1, 400).Select(i => $"line {i}")));
        var proposal = Complete(ReplaceRange("Big.cs", 1, 400, "line 1"), _root);

        proposal.AcceptCommand.Execute(null);

        proposal.IsLargeDeletion.ShouldBeTrue();
        proposal.ToolResultText.ShouldContain("399 removed");
        proposal.ToolResultText.ShouldContain("now 1 line(s) long (was 400)");
    }

    /// <summary>Auto Mode applies edits with nobody looking. Before it does, the model is told that
    /// a mass deletion is being held back and why - so it can say "that was not what I meant"
    /// rather than waiting silently.</summary>
    [Fact]
    public void Complete_WithAMassDeletion_TellsTheModelItIsHeldForConfirmation()
    {
        WriteFile("Big.cs", string.Join("\n", Enumerable.Range(1, 400).Select(i => $"line {i}")));

        var proposal = Complete(ReplaceRange("Big.cs", 1, 400, "line 1"), _root);

        proposal.IsLargeDeletion.ShouldBeTrue();
        proposal.IsDecided.ShouldBeFalse();
        proposal.AcceptCommand.CanExecute(null).ShouldBeTrue();
        proposal.ToolResultText.ShouldContain("Not applied yet");
        proposal.ToolResultText.ShouldContain("400 lines down to 1");
    }

    [Fact]
    public void Complete_WithAnOrdinaryEdit_IsNotFlaggedAsALargeDeletion()
    {
        WriteFile("Foo.cs", "a\nb\nc\nd\ne");

        Complete(ReplaceRange("Foo.cs", 2, 2, "B"), _root).IsLargeDeletion.ShouldBeFalse();
    }

    [Fact]
    public void Accept_WhenTheFileChangedAfterTheProposal_ReportsTheConflict()
    {
        var path = WriteFile("Foo.cs", "a\nb\nc");
        var proposal = Complete(ReplaceRange("Foo.cs", 2, 2, "B"), _root);
        File.WriteAllText(path, "totally different");

        proposal.AcceptCommand.Execute(null);

        proposal.WasApplied.ShouldBeFalse();
        proposal.ToolResultText.ShouldContain("changed after this edit was proposed");
        File.ReadAllText(path).ShouldBe("totally different");
    }

    [Fact]
    public void Reject_TellsTheModelNotToReapplyIt()
    {
        WriteFile("Foo.cs", "a\nb\nc");
        var proposal = Complete(ReplaceRange("Foo.cs", 2, 2, "B"), _root);

        proposal.RejectCommand.Execute(null);

        proposal.WasApplied.ShouldBeFalse();
        proposal.ToolResultText.ShouldContain("rejected");
        proposal.ToolResultText.ShouldContain("unless they ask");
    }

    /// <summary>The arguments are replayed verbatim so the call appears in history as the model
    /// actually made it.</summary>
    [Fact]
    public void Complete_KeepsTheArgumentsJsonForReplayIntoHistory()
    {
        WriteFile("Foo.cs", "a\nb\nc");
        const string arguments = """{"file":"Foo.cs","mode":"replace_range","start_line":2,"end_line":2,"new_text":"B"}""";

        Complete(ReplaceRange("Foo.cs", 2, 2, "B"), _root, arguments).ArgumentsJson.ShouldBe(arguments);
    }

    [Fact]
    public void Complete_WithAnAnchoredEdit_AppliesAndReportsIt()
    {
        WriteFile("Foo.cs", "int a = 1;\nint b = 2;");
        var proposal = Complete(
            new EditOperation("Foo.cs", "replace_text", null, null, "int b = 3;", "int b = 2;"), _root);

        proposal.AcceptCommand.Execute(null);

        proposal.WasApplied.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_root, "Foo.cs")).ShouldBe("int a = 1;\nint b = 3;");
    }
}
