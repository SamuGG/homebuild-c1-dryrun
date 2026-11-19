namespace HomeBuild.Domain;

/// <summary>
/// C10 Lab 1: when an extra can be chosen. It opens when its unlock milestone completes and closes when its close
/// milestone completes. Milestones complete in order (BuildProgress), so the latest completed milestone says it all.
/// </summary>
public readonly record struct ExtraWindow
{
    public ExtraWindow(Milestone unlocksAt, Milestone closesAt)
    {
        if (closesAt <= unlocksAt)
        {
            throw new ArgumentException($"An extra must close after it unlocks ({unlocksAt} → {closesAt}).", nameof(closesAt));
        }

        UnlocksAt = unlocksAt;
        ClosesAt = closesAt;
    }

    public Milestone UnlocksAt { get; }

    public Milestone ClosesAt { get; }

    /// <summary>Open from the unlock milestone's completion until the close milestone's: [unlock, close).</summary>
    public bool IsOpenAfter(Milestone? latestCompleted) =>
        latestCompleted is { } latest && latest >= UnlocksAt && latest < ClosesAt;
}
