using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Construction;

/// <summary>
/// Construction's tables, in schema <c>construction</c>. One row per <see cref="BuildProgress"/> aggregate, with its
/// milestones in a JSON column, so the aggregate loads, saves and versions as one row. Diary entries are their own table.
/// </summary>
internal sealed class ConstructionDbContext(DbContextOptions<ConstructionDbContext> options, ITenantContext tenant) : DbContext(options), ITenantScoped
{
    public const string Schema = "construction";

    /// <summary>Who completed the seeded milestones: a site manager's user ID.</summary>
    public const string SeedSiteManager = "seed-site-manager";

    private static readonly DateOnly SeedStart = new(2026, 6, 1);

    private static readonly string[] Subcontractors = ["Northgate Groundworks", "Ouse Timber Frames", "Minster Roofing", "Aire Electrical"];

    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public DbSet<BuildProgress> BuildProgress => Set<BuildProgress>();

    public DbSet<DiaryEntry> DiaryEntries => Set<DiaryEntry>();

    // Every save runs in a transaction, as in Sales, so TenantCommandInterceptor sets app.tenant_id at TransactionStarted.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ChangeTracker.StampTenant(tenant);
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.StampTenant(tenant);
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddStronglyTypedIds();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<BuildProgress>(progress =>
        {
            progress.ToTable("build_progress");
            progress.HasKey(p => p.UnitId);
            progress.Property(p => p.UnitId);
            progress.Property(p => p.DevelopmentId);
            progress.HasIndex(p => p.DevelopmentId);
            progress.OwnsMany(p => p.Milestones, milestone =>
            {
                milestone.ToJson();
                milestone.Property(m => m.Milestone).HasConversion<string>();
                milestone.Property(m => m.Status).HasConversion<string>();
                milestone.Property(m => m.PlannedOn);
                milestone.Property(m => m.CompletedOn);
                milestone.Property(m => m.CompletedBy);
                milestone.Property(m => m.Subcontractor);
                milestone.Property(m => m.SiteNotes);
                milestone.Property(m => m.OpenDefects);
            });
            progress.Navigation(p => p.Milestones).UsePropertyAccessMode(PropertyAccessMode.Field);
            // The aggregate is one row, so xmin versions the whole aggregate: two concurrent changes to any of its
            // milestones conflict (optimistic concurrency, as for Sales' units).
            progress.Property<uint>("Version").IsRowVersion();
        });

        modelBuilder.Entity<DiaryEntry>(entry =>
        {
            entry.ToTable("diary_entries");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.UnitId);
            entry.Property(e => e.WrittenOn);
            entry.Property(e => e.Text).HasMaxLength(4000);
            entry.HasIndex(e => new { e.UnitId, e.WrittenOn });
        });

        modelBuilder.Entity<BuildProgress>().HasTenant(this);
        modelBuilder.Entity<DiaryEntry>().HasTenant(this);
    }

    /// <summary>
    /// Replaces C5's in-memory read model: each seeded unit gets a BuildProgress, completed through the aggregate as
    /// far as C5's deterministic rule put it (the last byte of the unit ID), so every run sees the same board.
    /// </summary>
    internal static async Task SeedAsync(DbContext db, CancellationToken cancellationToken)
    {
        var context = (ConstructionDbContext)db;
        if (await context.BuildProgress.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        foreach (var unit in SeedData.Units())
        {
            var progress = SeededProgress(unit.Id, unit.DevelopmentId);
            context.Entry(context.BuildProgress.Add(progress).Entity).Property(TenantModel.TenantId).CurrentValue = SeedData.DemoTenant;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    internal static BuildProgress SeededProgress(UnitId unitId, DevelopmentId developmentId)
    {
        var progress = Domain.BuildProgress.Start(unitId, developmentId, SeedStart, m => Subcontractors[(int)m % Subcontractors.Length]);
        var done = unitId.Value.ToByteArray()[15] % (Domain.BuildProgress.Order.Count + 1);
        foreach (var milestone in Domain.BuildProgress.Order.Take(done))
        {
            if (progress.Complete(milestone, SeedSiteManager, progress[milestone].PlannedOn).Value is not MilestoneDone)
            {
                throw new InvalidOperationException($"Seed data broke a milestone rule at {milestone}.");
            }
        }

        if (done < Domain.BuildProgress.Order.Count)
        {
            progress.Note(Domain.BuildProgress.Order[done], "Awaiting inspection sign-off", 2);
        }

        return progress;
    }
}
