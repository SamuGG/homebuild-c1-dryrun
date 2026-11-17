namespace HomeBuild.Domain;

/// <summary>
/// A milestone's state (State pattern): each state decides which transitions it allows, so adding a state adds a class
/// instead of editing a switch. The hierarchy is closed, so a switch over it is still checked for exhaustiveness.
/// Business rules return results; they never throw.
/// </summary>
public closed record class MilestoneState
{
    public virtual MilestoneTransition Start() => Refuse("start");

    public virtual MilestoneTransition Complete(DateOnly completedOn, string siteManager) => Refuse("complete");

    public virtual MilestoneTransition Skip() => Refuse("skip");

    private MilestoneTransition Refuse(string action) => new TransitionRefused(this, $"A milestone that is {Describe(this)} cannot {action}.");

    /// <summary>How the state reads in a sentence. Exhaustive: a new state fails to compile until it is described.</summary>
    public static string Describe(MilestoneState state) => state switch
    {
        NotStarted => "not started",
        InProgress => "in progress",
        Completed => "completed",
        Skipped => "skipped",
    };
}

public sealed record NotStarted : MilestoneState
{
    public override MilestoneTransition Start() => new MilestoneMoved(new InProgress());

    /// <summary>Part 2 skips "utilities connected" for flats; a milestone can be skipped only before it starts.</summary>
    public override MilestoneTransition Skip() => new MilestoneMoved(new Skipped());
}

public sealed record InProgress : MilestoneState
{
    public override MilestoneTransition Complete(DateOnly completedOn, string siteManager) =>
        new MilestoneMoved(new Completed(completedOn, siteManager));
}

public sealed record Completed(DateOnly On, string SiteManager) : MilestoneState;

public sealed record Skipped : MilestoneState;

public sealed record MilestoneMoved(MilestoneState To);

public sealed record TransitionRefused(MilestoneState From, string Reason);

public union MilestoneTransition(MilestoneMoved, TransitionRefused);
