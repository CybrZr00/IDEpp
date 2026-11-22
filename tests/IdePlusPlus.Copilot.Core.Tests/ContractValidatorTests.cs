using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>
/// The designer's contract commit checked against its design: it may change the interface stubs the
/// design declares and nothing else. The design here has one stub (Lib/IGreeter.cs, b1) and two ordinary
/// files (Lib/Class1.cs, b1; Lib/Greeter.cs, b2).
/// </summary>
public sealed class ContractValidatorTests
{
    private static DesignPlan Plan(params bool[] isStub) => new(
        "A greeter.",
        ["b1", "b2"],
        [
            new DesignFile("Lib/IGreeter.cs", "b1", "The contract.", ["Has Greet(string)."], isStub.ElementAtOrDefault(0)),
            new DesignFile("Lib/Class1.cs", "b1", "Uses it.", ["Calls Greet."], isStub.ElementAtOrDefault(1)),
            new DesignFile("Lib/Greeter.cs", "b2", "Implements it.", ["Greets by name."], isStub.ElementAtOrDefault(2)),
        ],
        [],
        []);

    private static PipelineCheck<DesignPlan> Check(DesignPlan plan, string[] changed, string[]? outside = null) =>
        ContractValidator.Validate(plan, changed, outside ?? []);

    [Fact]
    public void ACommitThatChangesExactlyTheDeclaredStubs_Passes()
    {
        var check = Check(Plan(true, false, true), ["Lib/IGreeter.cs", "Lib/Greeter.cs"]);

        check.IsValid.ShouldBeTrue(check.Feedback);
        check.Value.ShouldNotBeNull();
    }

    [Fact]
    public void ADesignWithNoStubs_AndACommitThatChangesNothing_Passes()
    {
        Check(Plan(false, false, false), []).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AChangeToAFileThatIsNotAStub_IsRefused_WhetherOrNotTheDesignListsIt()
    {
        var check = Check(Plan(true, false, false), ["Lib/IGreeter.cs", "Lib/Class1.cs", "Lib/Surprise.cs"]);

        check.IsValid.ShouldBeFalse();
        check.Errors.Count.ShouldBe(2);
        check.Errors[0].ShouldContain("Lib/Class1.cs, which the design lists but does not declare as an interface stub");
        check.Errors[1].ShouldContain("Lib/Surprise.cs, which the design does not list");
    }

    [Fact]
    public void ADeclaredStubTheCommitDoesNotChange_IsRefused()
    {
        var check = Check(Plan(true, false, true), ["Lib/IGreeter.cs"]);

        check.Errors.ShouldHaveSingleItem().ShouldContain("declares Lib/Greeter.cs as an interface stub, but your commit does not change it");
    }

    [Fact]
    public void ACommitWithChangesAndADesignWithNoStubs_IsRefused_EachFileNamed()
    {
        var check = Check(Plan(false, false, false), ["Lib/Class1.cs"]);

        check.Errors.ShouldHaveSingleItem().ShouldContain("Lib/Class1.cs");
    }

    [Fact]
    public void ChangesOutsideTheSolutionFolder_AreRefused_ForNoDesignFileCanNameThem()
    {
        var check = Check(Plan(true, false, false), ["Lib/IGreeter.cs"], ["README.md", ".github/ci.yml"]);

        check.Errors.Count.ShouldBe(2);
        check.Errors.ShouldAllBe(e => e.Contains("outside the solution folder"));
        check.Errors[0].ShouldContain(".github/ci.yml"); // in order
    }

    [Fact]
    public void PathsAreComparedWithoutRegardToCase_AsOnWindows()
    {
        Check(Plan(true, false, false), ["lib/igreeter.CS"]).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce_NotOneAtATime()
    {
        var check = Check(Plan(true, false, true), ["Lib/Class1.cs"], ["x.txt"]);

        // One undeclared change, one outside change, two missing stubs.
        check.Errors.Count.ShouldBe(4);
    }
}
