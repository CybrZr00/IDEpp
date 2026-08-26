using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public class ReviewMessageViewModelTests
{
    [Fact]
    public void NewReview_IsEmptyAndNotComplete()
    {
        var review = new ReviewMessageViewModel();

        review.Text.ShouldBe("");
        review.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void Append_AccumulatesAcrossCalls()
    {
        var review = new ReviewMessageViewModel();

        review.Append("This looks ");
        review.Append("correct.");

        review.Text.ShouldBe("This looks correct.");
    }

    [Fact]
    public void IsComplete_IsSetExplicitlyRatherThanInferredFromText()
    {
        var review = new ReviewMessageViewModel();

        review.Append("still streaming");

        review.IsComplete.ShouldBeFalse();

        review.IsComplete = true;

        review.IsComplete.ShouldBeTrue();
    }
}
