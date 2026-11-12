using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Construction;

public enum MilestoneStatus
{
    NotStarted,
    InProgress,
    Done,
}

/// <summary>One milestone of one unit, as site staff see it. SiteNotes, Subcontractor and OpenDefects are internal.</summary>
internal sealed record UnitMilestone(
    string Milestone, MilestoneStatus Status, DateOnly? CompletedOn, string Subcontractor, string SiteNotes, int OpenDefects);

/// <summary>
/// C5's seeded milestone read model: a fixed, deterministic set of milestones per unit, in memory. C7 replaces it with
/// the build state machine and its commands; until then nothing writes to it.
/// </summary>
internal sealed class MilestoneReadModel
{
    public static readonly IReadOnlyList<string> Milestones =
        ["Foundations", "Frame", "Roof", "First fix", "Plastering", "Second fix", "Handover"];

    private static readonly string[] Subcontractors = ["Northgate Groundworks", "Ouse Timber Frames", "Minster Roofing", "Aire Electrical"];

    // Instance state, not static: the model is injected so C7 can replace it with a stateful one.
    private readonly DateOnly seedStart = new(2026, 6, 1);

    /// <summary>A unit's milestones: how far it has got is derived from its ID, so every run sees the same board.</summary>
    public IReadOnlyList<UnitMilestone> For(UnitId unitId)
    {
        var done = unitId.Value.ToByteArray()[15] % (Milestones.Count + 1); // 0..7 milestones complete
        return [.. Milestones.Select((name, i) => new UnitMilestone(
            name,
            i < done ? MilestoneStatus.Done : i == done ? MilestoneStatus.InProgress : MilestoneStatus.NotStarted,
            i < done ? seedStart.AddDays(21 * (i + 1)) : null,
            Subcontractors[i % Subcontractors.Length],
            i == done ? "Awaiting inspection sign-off" : "",
            i == done ? 2 : 0))];
    }
}
