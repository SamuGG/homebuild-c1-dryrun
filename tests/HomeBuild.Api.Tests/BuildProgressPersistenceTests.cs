using HomeBuild.Api.Modules.Construction;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Api.Tests;

/// <summary>C7 Lab 1: the BuildProgress aggregate saves and loads as one row, with the app role and RLS.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class BuildProgressPersistenceTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Build_progress_round_trips_and_keeps_its_rules_after_reload()
    {
        var cs = PostgresFixture.AppConnectionString(await database.CreateDatabaseAsync());
        var unit = UnitId.New();
        await using (var db = ConstructionModule.CreateDbContext(cs, TestTenants.Demo))
        {
            db.BuildProgress.Add(ABuildProgress().ForUnit(unit).CompletedThrough(2).Build());
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = ConstructionModule.CreateDbContext(cs, TestTenants.Demo))
        {
            var progress = await db.BuildProgress.SingleAsync(p => p.UnitId == unit, Ct);
            Assert.IsType<AlreadyCompleted>(progress.Complete(Milestone.Frame, "site-manager", progress[Milestone.Frame].PlannedOn).Value);
            Assert.IsType<MilestoneDone>(progress.Complete(Milestone.Roof, "site-manager", progress[Milestone.Roof].PlannedOn).Value);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = ConstructionModule.CreateDbContext(cs, TestTenants.Demo))
        {
            var progress = await db.BuildProgress.AsNoTracking().SingleAsync(p => p.UnitId == unit, Ct);
            Assert.Equal(
                [MilestoneStatus.Completed, MilestoneStatus.Completed, MilestoneStatus.Completed, MilestoneStatus.InProgress],
                progress.Milestones.Take(4).Select(m => m.Status));
        }

        await using (var other = ConstructionModule.CreateDbContext(cs, TestTenants.Other))
        {
            Assert.False(await other.BuildProgress.IgnoreQueryFilters().AnyAsync(p => p.UnitId == unit, Ct)); // RLS
        }
    }

    [Fact]
    public async Task Seed_replaces_the_C5_read_model_for_every_seeded_unit()
    {
        var cs = PostgresFixture.AppConnectionString(await database.CreateDatabaseAsync());
        await using var db = ConstructionModule.CreateDbContext(cs, TestTenants.Demo);

        var progress = await db.BuildProgress.AsNoTracking().ToListAsync(Ct);

        Assert.Equal(6, progress.Count);
        Assert.All(progress, p => Assert.Equal(BuildProgress.Order.Count, p.Milestones.Count));
    }
}
