using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Payments;

/// <summary>
/// Hosts Payments' core (HomeBuild.Payments.Core) in the API: registers its use cases and the adapters for its ports.
/// The core knows nothing of EF Core, Catalogue or the API.
/// </summary>
public static class PaymentsModule
{
    public const string Name = "Payments";

    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        services.AddDbContext<PaymentsDbContext>((sp, options) => Configure(options, HomeBuildDatabase.ConnectionString(sp))
            .AddInterceptors(new TenantCommandInterceptor(sp.GetRequiredService<ITenantContext>())));
        // Scoped: adapters over scoped DbContexts and Catalogue's scoped queries.
        services.AddScoped<IStagePaymentStore, EfStagePaymentStore>();
        services.AddScoped<IUnitTerms, CatalogueUnitTerms>();
        // Scoped: a handler of Construction's MilestoneCompleted (C7 Lab 5), resolved by the dispatcher in the request's scope.
        services.AddScoped<IHandleDomainEvent<MilestoneCompleted>, RecordStagePaymentDue>();
        return services;
    }

    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints) => endpoints;

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, PaymentsDbContext.Schema);

    internal static PaymentsDbContext CreateDbContext(string connectionString, Guid? tenantId = null)
    {
        var tenant = new FixedTenantContext(tenantId);
        return new((DbContextOptions<PaymentsDbContext>)Configure(new DbContextOptionsBuilder<PaymentsDbContext>(), connectionString)
            .AddInterceptors(new TenantCommandInterceptor(tenant)).Options, tenant);
    }

    /// <summary>
    /// The stage schedule of a development. No course has stored one yet, so it follows the development's deposit
    /// strategy key from C2: "fixed" developments take fixed stages, "percentage" ones percentages of the price.
    /// </summary>
    internal static IStagePaymentSchedule ScheduleFor(string key) => key == "percentage"
        ? new PercentageOfPrice([(Milestone.Foundations, 10m), (Milestone.Roof, 15m), (Milestone.FirstFix, 15m), (Milestone.SecondFix, 20m), (Milestone.PracticalCompletion, 40m)])
        : new FixedStages([(Milestone.Foundations, 10_000m), (Milestone.Roof, 15_000m)]);
}

/// <summary>Adapter for <see cref="IUnitTerms"/>: asks Catalogue's published queries.</summary>
internal sealed class CatalogueUnitTerms(ICatalogueQueries catalogue) : IUnitTerms
{
    public async Task<UnitTerms?> TermsOfAsync(UnitId unitId, CancellationToken cancellationToken) =>
        await catalogue.PriceOfAsync(unitId, cancellationToken) is { } unit
            ? new UnitTerms(unit.Price, PaymentsModule.ScheduleFor(await catalogue.DepositStrategyForAsync(unit.DevelopmentId, cancellationToken)))
            : null;
}

/// <summary>Adapter for <see cref="IStagePaymentStore"/>: EF Core, schema payments, one row per unit and milestone.</summary>
internal sealed class EfStagePaymentStore(PaymentsDbContext db) : IStagePaymentStore
{
    public Task<bool> ExistsAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken) =>
        db.StagePayments.AnyAsync(p => p.UnitId == unitId && p.Milestone == milestone, cancellationToken);

    public async Task<bool> AddAsync(DueStagePayment payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);
        db.StagePayments.Add(new StagePaymentRow
        {
            UnitId = payment.UnitId, Milestone = payment.Milestone, Amount = payment.Amount.Amount, Currency = payment.Amount.Currency, DueFrom = payment.DueFrom,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries of the same event raced past ExistsAsync: the unique key keeps one row.
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

internal sealed class StagePaymentRow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required UnitId UnitId { get; init; }

    public required Milestone Milestone { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required DateOnly DueFrom { get; init; }
}

/// <summary>Payments' tables in the API's database, schema payments (C7 Lab 5: due stage payments).</summary>
internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ITenantContext tenant) : DbContext(options), ITenantScoped
{
    public const string Schema = "payments";

    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public DbSet<StagePaymentRow> StagePayments => Set<StagePaymentRow>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.StampTenant(tenant);
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ChangeTracker.StampTenant(tenant);
        Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddStronglyTypedIds();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<StagePaymentRow>(payment =>
        {
            payment.ToTable("stage_payments_due");
            payment.HasKey(p => p.Id);
            payment.Property(p => p.Milestone).HasConversion<string>().HasMaxLength(40);
            payment.Property(p => p.Amount).HasPrecision(12, 2);
            payment.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            // Idempotency's final guard: one due payment per unit and milestone, however often the event arrives.
            payment.HasIndex(p => new { p.UnitId, p.Milestone }).IsUnique();
        });
        modelBuilder.Entity<StagePaymentRow>().HasTenant(this);
    }
}
