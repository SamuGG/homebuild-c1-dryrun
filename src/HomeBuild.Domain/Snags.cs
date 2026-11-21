namespace HomeBuild.Domain;

/// <summary>Who acts on a snag: the buyer, or Aftercare (staff). Pre-handover snags recorded by staff are Aftercare's.</summary>
public enum SnagActor
{
    Buyer,
    Aftercare,
}

public enum SnagAction
{
    Report,
    Acknowledge,
    Schedule,
    Fix,
    Confirm,
    Dispute,
    Close,
}

public enum SnagSeverity
{
    Minor,
    Major,
    Urgent,
}

/// <summary>
/// C10 Lab 4: a snag's state, a closed hierarchy (as C7's reservation states): a switch over it must name every state.
/// Scheduled carries the visit; Disputed carries the buyer's reason.
/// </summary>
public closed record class SnagState
{
    public sealed record Draft : SnagState;

    public sealed record Reported : SnagState;

    public sealed record Acknowledged : SnagState;

    public sealed record Scheduled(string Subcontractor, DateOnly VisitOn) : SnagState;

    public sealed record Fixed : SnagState;

    public sealed record Confirmed : SnagState;

    public sealed record Disputed(string Reason) : SnagState;

    public sealed record Closed : SnagState;

    public string Name => this switch
    {
        Draft => nameof(Draft),
        Reported => nameof(Reported),
        Acknowledged => nameof(Acknowledged),
        Scheduled => nameof(Scheduled),
        Fixed => nameof(Fixed),
        Confirmed => nameof(Confirmed),
        Disputed => nameof(Disputed),
        Closed => nameof(Closed),
    };
}

/// <summary>What an action needs beyond the actor: the visit (Schedule) or the reason (Dispute).</summary>
public sealed record SnagActionDetails(string? Subcontractor = null, DateOnly? VisitOn = null, string? Reason = null);

/// <summary>
/// The lifecycle as one table of (state, action, actor) → state. The code applies it and the tests read it, so the
/// diagram in docs/architecture, the code and the tests are three views of one table. Anything not in it is refused.
/// </summary>
public static class SnagLifecycle
{
    public sealed record Transition(string From, SnagAction Action, SnagActor Actor, string To);

    public static IReadOnlyList<Transition> Table { get; } =
    [
        new(nameof(SnagState.Draft), SnagAction.Report, SnagActor.Buyer, nameof(SnagState.Reported)),
        new(nameof(SnagState.Reported), SnagAction.Acknowledge, SnagActor.Aftercare, nameof(SnagState.Acknowledged)),
        new(nameof(SnagState.Acknowledged), SnagAction.Schedule, SnagActor.Aftercare, nameof(SnagState.Scheduled)),
        new(nameof(SnagState.Scheduled), SnagAction.Fix, SnagActor.Aftercare, nameof(SnagState.Fixed)),
        new(nameof(SnagState.Fixed), SnagAction.Confirm, SnagActor.Buyer, nameof(SnagState.Confirmed)),
        new(nameof(SnagState.Confirmed), SnagAction.Close, SnagActor.Aftercare, nameof(SnagState.Closed)),
        new(nameof(SnagState.Fixed), SnagAction.Dispute, SnagActor.Buyer, nameof(SnagState.Disputed)),
        new(nameof(SnagState.Disputed), SnagAction.Schedule, SnagActor.Aftercare, nameof(SnagState.Scheduled)),
    ];

    /// <summary>The state names, in lifecycle order.</summary>
    public static IReadOnlyList<string> States { get; } =
        [nameof(SnagState.Draft), nameof(SnagState.Reported), nameof(SnagState.Acknowledged), nameof(SnagState.Scheduled),
         nameof(SnagState.Fixed), nameof(SnagState.Confirmed), nameof(SnagState.Disputed), nameof(SnagState.Closed)];

    /// <summary>The next state, or why not: not in the table (wrong state or wrong actor), or details missing.</summary>
    public static SnagTransitionResult Apply(SnagState from, SnagAction action, SnagActor actor, SnagActionDetails details)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(details);
        if (Table.FirstOrDefault(t => t.From == from.Name && t.Action == action && t.Actor == actor) is not { } transition)
        {
            return new SnagRefused(from, Table.Any(t => t.From == from.Name && t.Action == action)
                ? $"Only {(actor == SnagActor.Buyer ? "Aftercare" : "the buyer")} may {action.ToString().ToLowerInvariant()} a snag that is {from.Name.ToLowerInvariant()}."
                : $"A snag that is {from.Name.ToLowerInvariant()} cannot be {Past(action)}.");
        }

        return transition.To switch
        {
            nameof(SnagState.Reported) => new SnagMoved(new SnagState.Reported()),
            nameof(SnagState.Acknowledged) => new SnagMoved(new SnagState.Acknowledged()),
            nameof(SnagState.Scheduled) when details is { Subcontractor: { Length: > 0 } who, VisitOn: { } on } => new SnagMoved(new SnagState.Scheduled(who, on)),
            nameof(SnagState.Scheduled) => new SnagRefused(from, "Scheduling needs a subcontractor and a visit date."),
            nameof(SnagState.Fixed) => new SnagMoved(new SnagState.Fixed()),
            nameof(SnagState.Confirmed) => new SnagMoved(new SnagState.Confirmed()),
            nameof(SnagState.Disputed) when details is { Reason: { Length: > 0 } reason } => new SnagMoved(new SnagState.Disputed(reason)),
            nameof(SnagState.Disputed) => new SnagRefused(from, "A dispute needs a reason."),
            nameof(SnagState.Closed) => new SnagMoved(new SnagState.Closed()),
            _ => throw new InvalidOperationException($"The table names an unknown state '{transition.To}'."),
        };
    }

    private static string Past(SnagAction action) => action switch
    {
        SnagAction.Report => "reported",
        SnagAction.Acknowledge => "acknowledged",
        SnagAction.Schedule => "scheduled",
        SnagAction.Fix => "fixed",
        SnagAction.Confirm => "confirmed",
        SnagAction.Dispute => "disputed",
        SnagAction.Close => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}

public sealed record SnagMoved(SnagState To);

public sealed record SnagRefused(SnagState From, string Reason);

public union SnagTransitionResult(SnagMoved, SnagRefused);

public enum SnagEntryKind
{
    Created,
    Transition,
    Comment,

    /// <summary>Aftercare's internal note: never in the buyer's view.</summary>
    InternalNote,
}

/// <summary>One line of a snag's timeline: who (actor and their ID), when, and what (a transition, a comment or a note).</summary>
public sealed record SnagEntry(Guid Id, SnagEntryKind Kind, SnagActor Actor, string ActorId, DateTimeOffset At, string Text, string? From = null, string? To = null)
{
    public bool IsInternal => Kind == SnagEntryKind.InternalNote;
}

/// <summary>
/// C10 Lab 4: a snag on a home, reported by its buyer (or recorded by staff before handover). It changes only through
/// its root: every action goes through <see cref="SnagLifecycle"/>, and every change adds a timeline entry with the
/// actor and the time, so the history settles a disagreement between the parties.
/// </summary>
public sealed class Snag
{
    private readonly List<SnagEntry> _timeline = [];

    private Snag(Guid id, UnitId unitId, string area, string description, SnagSeverity severity, SnagState state, bool beforeHandover, DateTimeOffset createdAt)
    {
        Id = id;
        UnitId = unitId;
        Area = area;
        Description = description;
        Severity = severity;
        State = state;
        BeforeHandover = beforeHandover;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public UnitId UnitId { get; private set; }

    public string Area { get; private set; }

    public string Description { get; private set; }

    public SnagSeverity Severity { get; private set; }

    public SnagState State { get; private set; }

    /// <summary>Recorded by staff before handover (C7's open defects, now in the same model).</summary>
    public bool BeforeHandover { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<SnagEntry> Timeline => _timeline;

    /// <summary>A buyer's new snag: a draft until they report it.</summary>
    public static Snag Draft(UnitId unitId, string area, string description, SnagSeverity severity, string buyerId, DateTimeOffset at)
    {
        var snag = new Snag(Guid.CreateVersion7(), unitId, area, description, severity, new SnagState.Draft(), beforeHandover: false, at);
        snag._timeline.Add(new SnagEntry(Guid.CreateVersion7(), SnagEntryKind.Created, SnagActor.Buyer, buyerId, at, "Snag drafted", To: nameof(SnagState.Draft)));
        return snag;
    }

    /// <summary>Staff record a snag before handover: it starts reported (there is no buyer draft).</summary>
    public static Snag RecordedBeforeHandover(UnitId unitId, string area, string description, SnagSeverity severity, string staffId, DateTimeOffset at)
    {
        var snag = new Snag(Guid.CreateVersion7(), unitId, area, description, severity, new SnagState.Reported(), beforeHandover: true, at);
        snag._timeline.Add(new SnagEntry(Guid.CreateVersion7(), SnagEntryKind.Created, SnagActor.Aftercare, staffId, at, "Snag recorded before handover", To: nameof(SnagState.Reported)));
        return snag;
    }

    public SnagTransitionResult Act(SnagAction action, SnagActor actor, string actorId, SnagActionDetails details, DateTimeOffset at)
    {
        var result = SnagLifecycle.Apply(State, action, actor, details);
        if (result.Value is SnagMoved moved)
        {
            var text = moved.To switch
            {
                SnagState.Scheduled s => $"Visit by {s.Subcontractor} on {s.VisitOn:d MMMM yyyy}",
                SnagState.Disputed d => d.Reason,
                _ => action.ToString(),
            };
            _timeline.Add(new SnagEntry(Guid.CreateVersion7(), SnagEntryKind.Transition, actor, actorId, at, text, State.Name, moved.To.Name));
            State = moved.To;
        }

        return result;
    }

    public void Comment(SnagActor actor, string actorId, string text, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _timeline.Add(new SnagEntry(Guid.CreateVersion7(), SnagEntryKind.Comment, actor, actorId, at, text));
    }

    /// <summary>Aftercare only (the caller checks the role); never shown to the buyer.</summary>
    public void InternalNote(string staffId, string text, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _timeline.Add(new SnagEntry(Guid.CreateVersion7(), SnagEntryKind.InternalNote, SnagActor.Aftercare, staffId, at, text));
    }
}
