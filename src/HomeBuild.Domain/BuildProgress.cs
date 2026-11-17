namespace HomeBuild.Domain;

/// <summary>A unit's build milestones, in the order they must complete.</summary>
public enum Milestone
{
    Foundations,
    Frame,
    Roof,
    FirstFix,
    Plastering,
    SecondFix,
    UtilitiesConnected,
    PracticalCompletion,
}

public enum MilestoneStatus
{
    NotStarted,
    InProgress,
    Completed,
    Skipped,
}

/// <summary>
/// One milestone of one unit. Part of the <see cref="BuildProgress"/> aggregate: it changes only through the root.
/// Subcontractor, site notes and open defects are the staff board's internal fields; no rule reads them.
/// </summary>
public sealed class MilestoneProgress
{
    internal MilestoneProgress(Milestone milestone, DateOnly plannedOn, string subcontractor)
    {
        Milestone = milestone;
        PlannedOn = plannedOn;
        Subcontractor = subcontractor;
    }

    // For EF Core.
    private MilestoneProgress()
    {
    }

    public Milestone Milestone { get; private set; }

    public MilestoneStatus Status { get; private set; }

    public DateOnly PlannedOn { get; private set; }

    public DateOnly? CompletedOn { get; private set; }

    /// <summary>The site manager who completed it: a user ID, not a user object (another aggregate, in Accounts).</summary>
    public string? CompletedBy { get; private set; }

    public string Subcontractor { get; private set; } = "";

    public string SiteNotes { get; private set; } = "";

    public int OpenDefects { get; private set; }

    /// <summary>The milestone's state object, rebuilt from the stored fields.</summary>
    public MilestoneState State => Status switch
    {
        MilestoneStatus.NotStarted => new NotStarted(),
        MilestoneStatus.InProgress => new InProgress(),
        MilestoneStatus.Completed => new Completed(CompletedOn!.Value, CompletedBy!),
        MilestoneStatus.Skipped => new Skipped(),
        _ => throw new InvalidOperationException($"Unknown milestone status {Status}."),
    };

    /// <summary>Applies a transition: a refused one changes nothing and is returned as it is.</summary>
    internal MilestoneTransition Apply(MilestoneTransition transition)
    {
        if (transition.Value is MilestoneMoved moved)
        {
            // Exhaustive over the closed hierarchy: a new state fails to compile until it is stored.
            (Status, CompletedOn, CompletedBy) = moved.To switch
            {
                NotStarted => (MilestoneStatus.NotStarted, (DateOnly?)null, (string?)null),
                InProgress => (MilestoneStatus.InProgress, null, null),
                Completed c => (MilestoneStatus.Completed, c.On, c.SiteManager),
                Skipped => (MilestoneStatus.Skipped, null, null),
            };
        }

        return transition;
    }

    /// <summary>Completed or skipped: nothing more to do, and the next milestone may follow.</summary>
    internal bool IsDone => State is Completed or Skipped;

    internal void Correct(DateOnly completedOn) => CompletedOn = completedOn;

    internal void Note(string siteNotes, int openDefects)
    {
        SiteNotes = siteNotes;
        OpenDefects = openDefects;
    }
}

/// <summary>
/// The build progress of one unit: the aggregate root for its milestones. The rules that must hold after every change
/// (milestones complete in order, never twice, never dated before the previous one, practical completion last) involve
/// only the milestones, so only they are inside. The unit, the development and the site manager are other aggregates,
/// referenced by ID. Diary entries and photos are evidence that no milestone rule reads, so they live in their own
/// aggregate (<see cref="DiaryEntry"/>) and point at the unit.
/// </summary>
public sealed class BuildProgress
{
    private readonly List<MilestoneProgress> _milestones = [];

    private BuildProgress(UnitId unitId, DevelopmentId developmentId)
    {
        UnitId = unitId;
        DevelopmentId = developmentId;
    }

    public static IReadOnlyList<Milestone> Order { get; } = Enum.GetValues<Milestone>();

    public UnitId UnitId { get; private set; }

    public DevelopmentId DevelopmentId { get; private set; }

    public IReadOnlyList<MilestoneProgress> Milestones => _milestones;

    /// <summary>Starts a unit's build: every milestone planned 21 days after the previous one, the first in progress.</summary>
    public static BuildProgress Start(UnitId unitId, DevelopmentId developmentId, DateOnly startOn, Func<Milestone, string>? subcontractor = null)
    {
        var progress = new BuildProgress(unitId, developmentId);
        foreach (var milestone in Order)
        {
            progress._milestones.Add(new MilestoneProgress(
                milestone, startOn.AddDays(21 * ((int)milestone + 1)), subcontractor?.Invoke(milestone) ?? ""));
        }

        progress._milestones[0].Apply(progress._milestones[0].State.Start());
        return progress;
    }

    public MilestoneProgress this[Milestone milestone] => _milestones[(int)milestone];

    /// <summary>
    /// Completes a milestone, or says which rule stops it. Business rules never throw. The aggregate checks the rules
    /// between milestones (order, dates, practical completion last); the milestone's state decides whether it can complete.
    /// </summary>
    public CompleteResult Complete(Milestone milestone, string siteManager, DateOnly on)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteManager);
        var target = this[milestone];
        var previous = milestone == Order[0] ? null : this[milestone - 1];
        if (target.State is Completed done)
        {
            return new AlreadyCompleted(milestone, done.On);
        }

        if (milestone == Milestone.PracticalCompletion && _milestones.Where(m => m.Milestone < milestone && !m.IsDone).Select(m => m.Milestone).ToList() is { Count: > 0 } open)
        {
            return new EarlierMilestonesOpen(milestone, open);
        }

        if (previous is { IsDone: false })
        {
            return new OutOfOrder(milestone, previous.Milestone);
        }

        if (previous?.State is Completed { On: var previousOn } && on < previousOn)
        {
            return new CompletedBeforePrevious(milestone, on, previous.Milestone, previousOn);
        }

        if (target.Apply(target.State.Complete(on, siteManager)).Value is TransitionRefused refused)
        {
            return new MilestoneNotCompletable(milestone, refused.Reason);
        }

        if (milestone != Order[^1])
        {
            var next = this[milestone + 1];
            next.Apply(next.State.Start());
        }

        return new MilestoneDone(milestone, on);
    }

    /// <summary>
    /// An admin's correction of a completed milestone's date. It needs a reason, and the corrected date must still sit
    /// between the previous and the next completed milestones. The caller records who corrected it and why (audit).
    /// </summary>
    public CorrectResult CorrectCompletion(Milestone milestone, DateOnly completedOn, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new ReasonRequired(milestone);
        }

        var target = this[milestone];
        if (target.State is not Completed completed)
        {
            return new NotCompleted(milestone, MilestoneState.Describe(target.State));
        }

        var previous = milestone == Order[0] ? null : this[milestone - 1];
        var next = milestone == Order[^1] ? null : this[milestone + 1];
        if ((previous?.State is Completed p && completedOn < p.On) || (next?.State is Completed n && completedOn > n.On))
        {
            return new CorrectionBreaksOrder(milestone, completedOn);
        }

        target.Correct(completedOn);
        return new CompletionCorrected(milestone, completed.On, completedOn, completed.SiteManager);
    }

    /// <summary>Records the site team's notes on a milestone (staff-only fields; no rule depends on them).</summary>
    public void Note(Milestone milestone, string siteNotes, int openDefects) => this[milestone].Note(siteNotes, openDefects);
}

public sealed record MilestoneDone(Milestone Milestone, DateOnly On);

public sealed record AlreadyCompleted(Milestone Milestone, DateOnly CompletedOn);

public sealed record OutOfOrder(Milestone Milestone, Milestone Waiting);

public sealed record CompletedBeforePrevious(Milestone Milestone, DateOnly On, Milestone Previous, DateOnly PreviousOn);

public sealed record EarlierMilestonesOpen(Milestone Milestone, IReadOnlyList<Milestone> Open);

/// <summary>The milestone's own state refused (for example, it was skipped).</summary>
public sealed record MilestoneNotCompletable(Milestone Milestone, string Reason);

public sealed record CompletionCorrected(Milestone Milestone, DateOnly From, DateOnly To, string CompletedBy);

public sealed record ReasonRequired(Milestone Milestone);

public sealed record NotCompleted(Milestone Milestone, string State);

public sealed record CorrectionBreaksOrder(Milestone Milestone, DateOnly CompletedOn);

public union CorrectResult(CompletionCorrected, ReasonRequired, NotCompleted, CorrectionBreaksOrder);

public union CompleteResult(MilestoneDone, AlreadyCompleted, OutOfOrder, CompletedBeforePrevious, EarlierMilestonesOpen, MilestoneNotCompletable);

/// <summary>
/// A site diary entry: its own aggregate, referencing the unit by ID. No milestone rule needs it in the same
/// transaction, so adding one never loads or locks the unit's milestones (C7 Lab 1). Lab 4 adds the weather, the
/// author and photos (S3 keys under diary/, uploaded straight from the browser as in C5).
/// </summary>
public sealed class DiaryEntry
{
    public DiaryEntry(DiaryEntryId id, UnitId unitId, DateOnly writtenOn, string text, string weather = "", string writtenBy = "", IReadOnlyList<string>? photoKeys = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Id = id;
        UnitId = unitId;
        WrittenOn = writtenOn;
        Text = text;
        Weather = weather;
        WrittenBy = writtenBy;
        PhotoKeys = [.. photoKeys ?? []];
    }

    // For EF Core.
    private DiaryEntry()
    {
        Text = "";
        Weather = "";
        WrittenBy = "";
        PhotoKeys = [];
    }

    public DiaryEntryId Id { get; private set; }

    public UnitId UnitId { get; private set; }

    public DateOnly WrittenOn { get; private set; }

    public string Text { get; private set; }

    public string Weather { get; private set; }

    /// <summary>The site manager who wrote it: a user ID.</summary>
    public string WrittenBy { get; private set; }

    public IReadOnlyList<string> PhotoKeys { get; private set; }
}
