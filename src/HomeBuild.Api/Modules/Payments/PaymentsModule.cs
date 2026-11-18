using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Configuration;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Payments.DepositSession;
using HomeBuild.Api.Modules.Payments.ListOverdue;
using HomeBuild.Api.Modules.Payments.MyDeposits;
using HomeBuild.Api.Modules.Payments.StripeWebhook;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

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
            .AddInterceptors(new TenantCommandInterceptor(sp.GetRequiredService<ITenantContext>()), sp.GetRequiredService<OpenTransactions>()));
        // Scoped: adapters over scoped DbContexts and Catalogue's scoped queries.
        services.AddScoped<IPaymentStore, EfStagePaymentStore>();
        services.AddScoped<IUnitTerms, CatalogueUnitTerms>();
        // Scoped: a handler of Construction's MilestoneCompleted (C7 Lab 5), resolved by the dispatcher in the request's scope.
        services.AddScoped<IHandleDomainEvent<MilestoneCompleted>, RecordStagePaymentDue>();
        services.AddScoped<ListOverdueStagePayments>();
        // C8 Lab 1: the Stripe adapter. Singleton client: StripeClient is thread-safe and reuses one HttpClient. The key
        // is read when the client is first used, so CI (which never has a Stripe key) starts, and tests swap the client.
        services.AddSingleton<IStripeClient>(sp => new StripeClient(
            sp.GetRequiredService<IOptions<PaymentsOptions>>().Value.SecretKey is { Length: > 0 } key
                ? key
                : throw new InvalidOperationException("Payments:SecretKey is not set (user-secrets locally, SSM in AWS).")));
        // Scoped: the use cases are per request, like the stores they sit beside.
        services.AddScoped<IPaymentGateway, StripePaymentGateway>();
        services.AddScoped<StartDepositCheckout>();
        // Scoped: the webhook's transaction script over the request's DbContexts (C8 Lab 2).
        services.AddScoped<StripeDepositWebhook>();
        // MarkPaid stays unregistered: no endpoint pays stage payments yet. Its tests use the fake gateway.
        return services;
    }

    public static IEndpointRouteBuilder MapPaymentsModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name);
        group.MapListOverdueStagePayments();
        group.MapDepositSession();
        group.MapMyDeposits(); // C8 Lab 5
        // Not versioned: Stripe calls one fixed URL. Anonymous, but every request must carry Stripe's signature.
        endpoints.MapStripeWebhook().WithTags(Name);
        return endpoints;
    }

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

/// <summary>Adapter for <see cref="IPaymentStore"/>: EF Core, schema payments, one row per unit and milestone.</summary>
internal sealed class EfStagePaymentStore(PaymentsDbContext db) : IPaymentStore
{
    public Task<bool> ExistsAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken) =>
        db.StagePayments.AnyAsync(p => p.UnitId == unitId && p.Milestone == milestone, cancellationToken);

    public async Task<StoredStagePayment?> FindAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken) =>
        await db.StagePayments.AsNoTracking().SingleOrDefaultAsync(p => p.UnitId == unitId && p.Milestone == milestone, cancellationToken) is { } row
            ? new StoredStagePayment(new DueStagePayment(row.UnitId, row.Milestone, new Money(row.Amount, row.Currency), row.DueFrom), row.PaidAt, row.ProviderReference)
            : null;

    public Task MarkPaidAsync(UnitId unitId, Milestone milestone, string providerReference, DateTimeOffset paidAt, CancellationToken cancellationToken) =>
        db.StagePayments.Where(p => p.UnitId == unitId && p.Milestone == milestone && p.PaidAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.PaidAt, paidAt).SetProperty(p => p.ProviderReference, providerReference), cancellationToken);

    public async Task<IReadOnlyList<DueStagePayment>> UnpaidDueOnOrBeforeAsync(DateOnly dueOnOrBefore, CancellationToken cancellationToken) =>
        [.. (await db.StagePayments.AsNoTracking().Where(p => p.PaidAt == null && p.DueFrom <= dueOnOrBefore).OrderBy(p => p.DueFrom).ToListAsync(cancellationToken))
            .Select(row => new DueStagePayment(row.UnitId, row.Milestone, new Money(row.Amount, row.Currency), row.DueFrom))];

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

/// <summary>A Stripe event HomeBuild has processed: the unique event ID makes a redelivery a no-op (C8 Lab 2).</summary>
internal sealed class ProcessedWebhookEvent
{
    public required string EventId { get; init; }

    public required string Type { get; init; }

    public required DateTimeOffset ProcessedAt { get; init; }
}

/// <summary>A paid deposit (<see cref="HomeBuild.Payments.DepositPaid"/>): one row per paid Checkout Session.</summary>
internal sealed class DepositRow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required ReservationId ReservationId { get; init; }

    public required string SessionId { get; init; }

    public required string ProviderReference { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required DateTimeOffset PaidAt { get; init; }

    /// <summary>C8 Lab 5: why this payment is being given back (set with the deposit, in the webhook's transaction).</summary>
    public string? RefundReason { get; init; }

    /// <summary>Stripe's refund ID, once the refund call (after the commit) succeeded. Reason without it: pending.</summary>
    public string? RefundReference { get; init; }

    public DateTimeOffset? RefundedAt { get; init; }
}

/// <summary>A Checkout Session that closed unpaid: payment did not happen. The reservation's hold is untouched.</summary>
internal sealed class ExpiredCheckoutSession
{
    public required string SessionId { get; init; }

    public required ReservationId ReservationId { get; init; }

    public required DateTimeOffset ExpiredAt { get; init; }
}

internal sealed class StagePaymentRow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required UnitId UnitId { get; init; }

    public required Milestone Milestone { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required DateOnly DueFrom { get; init; }

    public DateTimeOffset? PaidAt { get; init; }

    public string? ProviderReference { get; init; }
}

/// <summary>Payments' tables in the API's database, schema payments (C7 Lab 5: due stage payments).</summary>
internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ITenantContext tenant) : DbContext(options), ITenantScoped
{
    public const string Schema = "payments";

    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public DbSet<StagePaymentRow> StagePayments => Set<StagePaymentRow>();

    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();

    public DbSet<DepositRow> Deposits => Set<DepositRow>();

    public DbSet<ExpiredCheckoutSession> ExpiredCheckoutSessions => Set<ExpiredCheckoutSession>();

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
            payment.Property(p => p.ProviderReference).HasMaxLength(200);
            // Idempotency's final guard: one due payment per unit and milestone, however often the event arrives.
            payment.HasIndex(p => new { p.UnitId, p.Milestone }).IsUnique();
        });
        modelBuilder.Entity<StagePaymentRow>().HasTenant(this);

        // C8 Lab 2: the webhook's tables.
        modelBuilder.Entity<ProcessedWebhookEvent>(e =>
        {
            e.ToTable("processed_webhook_events");
            e.HasKey(x => x.EventId); // unique event ID: the idempotency guard
            e.Property(x => x.EventId).HasMaxLength(255);
            e.Property(x => x.Type).HasMaxLength(100);
        });
        modelBuilder.Entity<DepositRow>(d =>
        {
            d.ToTable("deposits");
            d.HasKey(x => x.Id);
            d.HasIndex(x => x.SessionId).IsUnique(); // one deposit per paid session
            d.HasIndex(x => x.ReservationId);
            d.Property(x => x.SessionId).HasMaxLength(255);
            d.Property(x => x.ProviderReference).HasMaxLength(255);
            d.Property(x => x.Amount).HasPrecision(12, 2);
            d.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            d.Property(x => x.RefundReason).HasMaxLength(500);
            d.Property(x => x.RefundReference).HasMaxLength(255);
        });
        modelBuilder.Entity<ExpiredCheckoutSession>(x =>
        {
            x.ToTable("expired_checkout_sessions");
            x.HasKey(s => s.SessionId);
            x.Property(s => s.SessionId).HasMaxLength(255);
            x.HasIndex(s => s.ReservationId);
        });
        modelBuilder.Entity<ProcessedWebhookEvent>().HasTenant(this);
        modelBuilder.Entity<DepositRow>().HasTenant(this);
        modelBuilder.Entity<ExpiredCheckoutSession>().HasTenant(this);
        modelBuilder.HasOutbox(); // C9 Lab 2
    }
}
