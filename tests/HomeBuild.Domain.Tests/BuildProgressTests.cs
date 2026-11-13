using static HomeBuild.Testing.Builders;

namespace HomeBuild.Domain.Tests;

/// <summary>C7 Lab 1: the BuildProgress aggregate's rules, tested only through its root.</summary>
public class BuildProgressTests
{
    private const string SiteManager = "site-manager";

    [Fact]
    public void Milestones_complete_in_order()
    {
        var progress = ABuildProgress().Build();

        foreach (var milestone in BuildProgress.Order)
        {
            var result = progress.Complete(milestone, SiteManager, progress[milestone].PlannedOn);
            Assert.Equal(new MilestoneDone(milestone, progress[milestone].PlannedOn), result.Value);
        }

        Assert.All(progress.Milestones, m => Assert.Equal(MilestoneStatus.Completed, m.Status));
    }

    [Fact]
    public void Milestone_out_of_order_is_rejected_and_changes_nothing()
    {
        var progress = ABuildProgress().CompletedThrough(1).Build();

        var result = progress.Complete(Milestone.Roof, SiteManager, progress[Milestone.Roof].PlannedOn);

        Assert.Equal(new OutOfOrder(Milestone.Roof, Milestone.Frame), result.Value);
        Assert.Equal(MilestoneStatus.InProgress, progress[Milestone.Frame].Status);
        Assert.Equal(MilestoneStatus.NotStarted, progress[Milestone.Roof].Status);
    }

    [Fact]
    public void Milestone_cannot_complete_before_the_previous_one()
    {
        var progress = ABuildProgress().CompletedThrough(1).Build();
        var foundationsOn = progress[Milestone.Foundations].CompletedOn!.Value;

        var result = progress.Complete(Milestone.Frame, SiteManager, foundationsOn.AddDays(-1));

        Assert.Equal(new CompletedBeforePrevious(Milestone.Frame, foundationsOn.AddDays(-1), Milestone.Foundations, foundationsOn), result.Value);
        Assert.Equal(MilestoneStatus.InProgress, progress[Milestone.Frame].Status);
    }

    [Fact]
    public void Cannot_complete_milestone_twice()
    {
        var progress = ABuildProgress().CompletedThrough(2).Build();
        var frameOn = progress[Milestone.Frame].CompletedOn!.Value;

        var result = progress.Complete(Milestone.Frame, "another-site-manager", frameOn.AddDays(3));

        Assert.Equal(new AlreadyCompleted(Milestone.Frame, frameOn), result.Value);
        Assert.Equal(SiteManager, progress[Milestone.Frame].CompletedBy);
        Assert.Equal(frameOn, progress[Milestone.Frame].CompletedOn);
    }

    [Fact]
    public void Practical_completion_requires_all_previous()
    {
        var progress = ABuildProgress().CompletedThrough(BuildProgress.Order.Count - 2).Build();

        var result = progress.Complete(Milestone.PracticalCompletion, SiteManager, progress[Milestone.PracticalCompletion].PlannedOn);

        Assert.Equal(Milestone.PracticalCompletion, Assert.IsType<EarlierMilestonesOpen>(result.Value).Milestone);
        Assert.Equal([Milestone.UtilitiesConnected], ((EarlierMilestonesOpen)result.Value!).Open);
        Assert.Equal(MilestoneStatus.NotStarted, progress[Milestone.PracticalCompletion].Status);
    }

    [Fact]
    public void Completing_a_milestone_starts_the_next_and_records_who_and_when()
    {
        var progress = ABuildProgress().Build();
        var on = new DateOnly(2026, 6, 20);

        progress.Complete(Milestone.Foundations, SiteManager, on);

        Assert.Equal((MilestoneStatus.Completed, on, SiteManager), (progress[Milestone.Foundations].Status, progress[Milestone.Foundations].CompletedOn, progress[Milestone.Foundations].CompletedBy));
        Assert.Equal(MilestoneStatus.InProgress, progress[Milestone.Frame].Status);
    }
}
