using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Construction;

/// <summary>Reads build progress for the board and the buyer view (no tracking: nothing here changes it).</summary>
internal sealed class BuildProgressReader(ConstructionDbContext db)
{
    /// <summary>
    /// The progress of each unit. A unit with no build started yet (one added after seeding) shows a fresh,
    /// unsaved BuildProgress: every milestone not started except the first.
    /// </summary>
    public async Task<IReadOnlyDictionary<UnitId, BuildProgress>> ForAsync(
        IReadOnlyCollection<(UnitId Unit, DevelopmentId Development)> units, CancellationToken cancellationToken)
    {
        var ids = units.Select(u => u.Unit).ToList();
        var saved = await db.BuildProgress.AsNoTracking().Where(p => ids.Contains(p.UnitId)).ToDictionaryAsync(p => p.UnitId, cancellationToken);
        return units.ToDictionary(u => u.Unit, u => saved.TryGetValue(u.Unit, out var progress)
            ? progress
            : BuildProgress.Start(u.Unit, u.Development, DateOnly.FromDateTime(DateTime.UnixEpoch)));
    }

    /// <summary>
    /// The units among <paramref name="unitIds"/> whose saved build progress meets <paramref name="specification"/>, filtered
    /// in PostgreSQL. A unit whose build has not started has no progress to meet a specification, so it never matches.
    /// </summary>
    public async Task<HashSet<UnitId>> MatchingAsync(
        Specification<BuildProgress> specification, IReadOnlyCollection<UnitId> unitIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        var ids = unitIds.ToList();
        return [.. await db.BuildProgress.Where(p => ids.Contains(p.UnitId)).Where(specification.ToExpression())
            .Select(p => p.UnitId).ToListAsync(cancellationToken)];
    }

    /// <summary>The board's named filters: the specifications site staff ask for, or null for an unknown name.</summary>
    public static Specification<BuildProgress>? Filter(string show, DateOnly today) => show switch
    {
        "behind-schedule" => new BehindSchedule(today),
        "ready-for-handover" => BoardSpecifications.ReadyForHandover(),
        _ => null,
    };

    /// <summary>The name the board and the buyer view show for a milestone (C5's wording).</summary>
    public static string DisplayName(Milestone milestone) => milestone switch
    {
        Milestone.Foundations => "Foundations",
        Milestone.Frame => "Frame",
        Milestone.Roof => "Roof",
        Milestone.FirstFix => "First fix",
        Milestone.Plastering => "Plastering",
        Milestone.SecondFix => "Second fix",
        Milestone.UtilitiesConnected => "Utilities connected",
        Milestone.PracticalCompletion => "Practical completion",
        _ => milestone.ToString(),
    };

    /// <summary>The status string C5's board and web app read: "Done" for a completed milestone, unchanged.</summary>
    public static string BoardStatus(MilestoneStatus status) => status == MilestoneStatus.Completed ? "Done" : status.ToString();
}
