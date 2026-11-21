namespace HomeBuild.Domain.Tests;

/// <summary>
/// C10 Lab 4: the snag lifecycle, table-driven. The table below is the brief's, written out independently of
/// <see cref="SnagLifecycle.Table"/>: every allowed (state, action, actor) moves to its state; every other one of the
/// 8 × 7 × 2 combinations is refused.
/// </summary>
public class SnagLifecycleTests
{
    // Draft (buyer) → Reported (buyer) → Acknowledged (Aftercare) → Scheduled (Aftercare) → Fixed (Aftercare) →
    // Confirmed (buyer) → Closed (Aftercare), or Fixed → Disputed (buyer) → Scheduled again (Aftercare).
    private static readonly (string From, SnagAction Action, SnagActor Actor, string To)[] Brief =
    [
        ("Draft", SnagAction.Report, SnagActor.Buyer, "Reported"),
        ("Reported", SnagAction.Acknowledge, SnagActor.Aftercare, "Acknowledged"),
        ("Acknowledged", SnagAction.Schedule, SnagActor.Aftercare, "Scheduled"),
        ("Scheduled", SnagAction.Fix, SnagActor.Aftercare, "Fixed"),
        ("Fixed", SnagAction.Confirm, SnagActor.Buyer, "Confirmed"),
        ("Confirmed", SnagAction.Close, SnagActor.Aftercare, "Closed"),
        ("Fixed", SnagAction.Dispute, SnagActor.Buyer, "Disputed"),
        ("Disputed", SnagAction.Schedule, SnagActor.Aftercare, "Scheduled"),
    ];

    private static readonly SnagActionDetails Details = new("Northgate Plastering", new DateOnly(2026, 12, 1), "The crack is still there");

    private static SnagState StateNamed(string name) => name switch
    {
        "Draft" => new SnagState.Draft(),
        "Reported" => new SnagState.Reported(),
        "Acknowledged" => new SnagState.Acknowledged(),
        "Scheduled" => new SnagState.Scheduled("Northgate Plastering", new DateOnly(2026, 11, 25)),
        "Fixed" => new SnagState.Fixed(),
        "Confirmed" => new SnagState.Confirmed(),
        "Disputed" => new SnagState.Disputed("Not fixed"),
        "Closed" => new SnagState.Closed(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static TheoryData<string, SnagAction, SnagActor, string> Allowed() =>
        [.. Brief.Select(t => (t.From, t.Action, t.Actor, t.To))];

    public static TheoryData<string, SnagAction, SnagActor> Refused() =>
        [.. from state in SnagLifecycle.States
            from action in Enum.GetValues<SnagAction>()
            from actor in Enum.GetValues<SnagActor>()
            where !Brief.Any(t => t.From == state && t.Action == action && t.Actor == actor)
            select (state, action, actor)];

    // Every transition and actor in the table.
    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allowed_transition_moves_to_its_state(string from, SnagAction action, SnagActor actor, string to)
    {
        var result = SnagLifecycle.Apply(StateNamed(from), action, actor, Details);

        Assert.Equal(to, Assert.IsType<SnagMoved>(result.Value).To.Name);
    }

    // Every refused state, action and actor: the other 104 combinations.
    [Theory]
    [MemberData(nameof(Refused))]
    public void Refused_state_action_and_actor_is_refused(string from, SnagAction action, SnagActor actor)
    {
        var result = SnagLifecycle.Apply(StateNamed(from), action, actor, Details);

        Assert.IsType<SnagRefused>(result.Value);
    }

    [Fact]
    public void Table_has_every_combination_once()
    {
        Assert.Equal(8 * 7 * 2, Allowed().Count + Refused().Count);
        Assert.Equal(Brief.Select(t => (t.From, t.Action, t.Actor, t.To)), SnagLifecycle.Table.Select(t => (t.From, t.Action, t.Actor, t.To)));
    }

    [Fact]
    public void Buyer_cannot_schedule()
    {
        var snag = Reported();
        snag.Act(SnagAction.Acknowledge, SnagActor.Aftercare, "aftercare-1", Details, Now);

        var result = snag.Act(SnagAction.Schedule, SnagActor.Buyer, "buyer-1", Details, Now);

        Assert.Equal("Only Aftercare may schedule a snag that is acknowledged.", Assert.IsType<SnagRefused>(result.Value).Reason);
        Assert.IsType<SnagState.Acknowledged>(snag.State);
    }

    [Fact]
    public void Disputed_returns_to_scheduled()
    {
        var snag = Reported();
        snag.Act(SnagAction.Acknowledge, SnagActor.Aftercare, "aftercare-1", Details, Now);
        snag.Act(SnagAction.Schedule, SnagActor.Aftercare, "aftercare-1", new("Northgate Plastering", new DateOnly(2026, 11, 25)), Now);
        snag.Act(SnagAction.Fix, SnagActor.Aftercare, "aftercare-1", Details, Now);
        snag.Act(SnagAction.Dispute, SnagActor.Buyer, "buyer-1", new(Reason: "The crack is still there"), Now);

        var result = snag.Act(SnagAction.Schedule, SnagActor.Aftercare, "aftercare-1", new("Northgate Plastering", new DateOnly(2026, 12, 1)), Now);

        var scheduled = Assert.IsType<SnagState.Scheduled>(Assert.IsType<SnagMoved>(result.Value).To);
        Assert.Equal(new DateOnly(2026, 12, 1), scheduled.VisitOn);
        Assert.Equal(["Draft→Reported", "Reported→Acknowledged", "Acknowledged→Scheduled", "Scheduled→Fixed", "Fixed→Disputed", "Disputed→Scheduled"],
            snag.Timeline.Where(e => e.Kind == SnagEntryKind.Transition).Select(e => $"{e.From}→{e.To}"));
        Assert.Equal("The crack is still there", snag.Timeline.Single(e => e.To == "Disputed").Text);
    }

    [Fact]
    public void Dispute_needs_a_reason_and_schedule_needs_a_visit()
    {
        Assert.Equal("A dispute needs a reason.", Assert.IsType<SnagRefused>(SnagLifecycle.Apply(new SnagState.Fixed(), SnagAction.Dispute, SnagActor.Buyer, new()).Value).Reason);
        Assert.Equal("Scheduling needs a subcontractor and a visit date.", Assert.IsType<SnagRefused>(SnagLifecycle.Apply(new SnagState.Acknowledged(), SnagAction.Schedule, SnagActor.Aftercare, new()).Value).Reason);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static Snag Reported()
    {
        var snag = Snag.Draft(new UnitId(Guid.NewGuid()), "Kitchen", "Crack in the ceiling", SnagSeverity.Minor, "buyer-1", Now);
        snag.Act(SnagAction.Report, SnagActor.Buyer, "buyer-1", Details, Now);
        return snag;
    }
}
