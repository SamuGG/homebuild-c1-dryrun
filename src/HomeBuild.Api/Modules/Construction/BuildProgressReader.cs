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
