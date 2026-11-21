using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using HomeBuild.Messaging;

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

    /// <summary>C10 Lab 3: change orders from paid extras.</summary>
    internal DbSet<ChangeOrderRow> ChangeOrders => Set<ChangeOrderRow>();

    public DbSet<DiaryEntry> DiaryEntries => Set<DiaryEntry>();

    public DbSet<SiteAssignment> SiteAssignments => Set<SiteAssignment>();

    public DbSet<MilestoneAudit> MilestoneAudits => Set<MilestoneAudit>();

    // Every save runs in a transaction, as in Sales, so TenantCommandInterceptor sets app.tenant_id at TransactionStarted.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StageOutboxMessages();
        ChangeTracker.StampTenant(tenant);
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StageOutboxMessages();
        ChangeTracker.StampTenant(tenant);
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// C9 Lab 2: every MilestoneCompleted leaves the module through the outbox, written in this same SaveChanges (and so
    /// the same transaction) as the milestone. C7's in-process dispatch still runs after the commit (see
    /// docs/construction/domain-events.md) until each handler's consumer reads the queue instead.
    /// </summary>
    private void StageOutboxMessages()
    {
        foreach (var entry in ChangeTracker.Entries<BuildProgress>().ToList())
        {
            // The aggregate's own tenant (a seed or a tool has no request tenant).
            var tenantId = (Guid?)entry.Property(TenantModel.TenantId).CurrentValue is { } t && t != Guid.Empty ? t : CurrentTenantId;
            foreach (var completed in entry.Entity.DomainEvents.OfType<MilestoneCompleted>())
            {
                Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.MilestoneCompleted,
                    new MilestoneCompletedMessage(completed.UnitId.Value, tenantId, completed.Milestone.ToString(), completed.CompletedAt, completed.Version, entry.Entity.DevelopmentId.Value),
                    tenantId, DateTimeOffset.UtcNow));
            }
        }
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
            progress.Property<uint>("RowVersion").IsRowVersion();
            // C9 Lab 5: the domain version events carry (xmin is a transaction ID, unknown until the save commits).
            progress.Property(p => p.Version);
            progress.Ignore(p => p.DomainEvents); // dispatched after commit, never stored (C9's outbox will store them)
        });

        modelBuilder.Entity<DiaryEntry>(entry =>
        {
            entry.ToTable("diary_entries");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.UnitId);
            entry.Property(e => e.WrittenOn);
            entry.Property(e => e.Text).HasMaxLength(4000);
            entry.Property(e => e.Weather).HasMaxLength(100);
            entry.Property(e => e.WrittenBy).HasMaxLength(100);
            entry.Property(e => e.PhotoKeys);
            entry.HasIndex(e => new { e.UnitId, e.WrittenOn });
        });

        modelBuilder.Entity<SiteAssignment>(assignment =>
        {
            assignment.ToTable("site_assignments");
            assignment.HasKey(a => new { a.SiteManager, a.DevelopmentId });
            assignment.Property(a => a.SiteManager).HasMaxLength(100);
        });

        modelBuilder.Entity<MilestoneAudit>(audit =>
        {
            audit.ToTable("milestone_audit");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Milestone).HasConversion<string>().HasMaxLength(40);
            audit.Property(a => a.Action).HasMaxLength(20);
            audit.Property(a => a.By).HasMaxLength(100);
            audit.Property(a => a.Reason).HasMaxLength(1000);
            audit.HasIndex(a => new { a.UnitId, a.At });
        });

        modelBuilder.Entity<ChangeOrderRow>(order =>
        {
            order.ToTable("change_orders");
            order.HasKey(c => c.Id);
            order.Property(c => c.Id).ValueGeneratedNever();
            order.HasIndex(c => c.BasketLineId).IsUnique(); // one change order per paid line: the idempotency key
            order.HasIndex(c => c.DevelopmentId);
            order.Property(c => c.ExtraName).HasMaxLength(100);
            order.Property(c => c.FitBy).HasConversion<string>().HasMaxLength(40);
            order.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            order.Property(c => c.AcknowledgedBy).HasMaxLength(100);
        });
        modelBuilder.Entity<ChangeOrderRow>().HasTenant(this);
        modelBuilder.Entity<BuildProgress>().HasTenant(this);
        modelBuilder.HasOutbox(); // C9 Lab 2
        modelBuilder.Entity<DiaryEntry>().HasTenant(this);
        modelBuilder.Entity<SiteAssignment>().HasTenant(this);
        modelBuilder.Entity<MilestoneAudit>().HasTenant(this);
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

        context.Entry(context.SiteAssignments.Add(new SiteAssignment(SeedData.RiversideSiteManager, SeedData.Riverside)).Entity)
            .Property(TenantModel.TenantId).CurrentValue = SeedData.DemoTenant;
        context.Entry(context.SiteAssignments.Add(new SiteAssignment(SeedData.HillcrestSiteManager, SeedData.Hillcrest)).Entity)
            .Property(TenantModel.TenantId).CurrentValue = SeedData.DemoTenant;
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

        progress.ClearDomainEvents(); // seed data, not things that happened: nothing to publish (C9 Lab 2)
        return progress;
    }
}
