using System.Text.Json;
using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeBuild.Api.Modules.Extras;

/// <summary>
/// C10 Lab 1: extras (kitchen upgrades, flooring, solar…) that buyers can choose while their home's build is between an
/// extra's unlock and close milestones. Each development has its own catalogue. Extras never asks Construction about
/// progress: a consumer of MilestoneCompleted (queue homebuild-extras) keeps its own copy of each unit's latest
/// milestone, and an extra is open when that copy says so. Newly open extras leave as ExtrasUnlocked through the outbox,
/// and the notifications Lambda tells the buyer. Replaces C7's in-memory stub.
/// </summary>
public static class ExtrasModule
{
    public const string Name = "Extras";

    public static IServiceCollection AddExtrasModule(this IServiceCollection services)
    {
        services.AddDbContext<ExtrasDbContext>((sp, options) => Configure(options, HomeBuildDatabase.ConnectionString(sp))
            .AddInterceptors(new TenantCommandInterceptor(sp.GetRequiredService<ITenantContext>()), sp.GetRequiredService<OpenTransactions>()));
        // Keyed scoped: the queue consumer resolves one per message, in its own scope.
        services.AddKeyedScoped<IMessageHandler, ExtrasProgressConsumer>(ExtrasProgressConsumer.Name);
        // C10 Lab 2: Extras' published write for Payments' webhook (inside its transaction).
        services.AddScoped<IExtrasBasketPayments, EfExtrasBasketPayments>();
        return services;
    }

    public static IEndpointRouteBuilder MapExtrasModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name);
        group.MapGet("/me/extras", MyExtrasAsync)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("ListMyExtras")
            .WithSummary("The extras the signed-in buyer can choose now, per home (v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanReadOwnProgress);
        group.MapGet("/developments/{developmentId}/extras", DevelopmentExtrasAsync)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("ListDevelopmentExtras")
            .WithSummary("Every extra of a development with its unlock and close milestones (staff, v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanViewExtras);
        group.MapBasket(); // C10 Lab 2
        return endpoints;
    }

    /// <summary>The homebuild-extras consumer, in the API host (it writes Extras' tables), when Extras:QueueUrl is set.</summary>
    public static IServiceCollection AddExtrasConsumer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration["Extras:QueueUrl"] is { Length: > 0 } queueUrl)
        {
            services.TryAddSingleton<Amazon.SQS.IAmazonSQS>(_ => new Amazon.SQS.AmazonSQSClient(Amazon.RegionEndpoint.EUWest2));
            var options = configuration.GetSection("Extras:Consumer").Get<ConsumerOptions>() ?? new ConsumerOptions { Processors = 2, VisibilityTimeout = TimeSpan.FromMinutes(1) };
            // AddSingleton, not AddHostedService: that adds through TryAddEnumerable, one per implementation type, so a second QueueConsumer in the host was dropped (C10 Lab 7).
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new QueueConsumer(ExtrasProgressConsumer.Name, new SqsQueueClient(sp.GetRequiredService<Amazon.SQS.IAmazonSQS>(), queueUrl),
                sp.GetRequiredService<IServiceScopeFactory>(), options, sp.GetRequiredService<ILogger<QueueConsumer>>(), readiness: sp.GetService<ConsumerReadiness>()));
        }

        return services;
    }

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, ExtrasDbContext.Schema)
            .UseSeeding((db, _) => ExtrasDbContext.SeedAsync((ExtrasDbContext)db, CancellationToken.None).GetAwaiter().GetResult())
            .UseAsyncSeeding((db, _, ct) => ExtrasDbContext.SeedAsync((ExtrasDbContext)db, ct));

    /// <summary>A context outside a request, for tools and tests; <paramref name="tenantId"/> null means no tenant.</summary>
    internal static ExtrasDbContext CreateDbContext(string connectionString, Guid? tenantId = null)
    {
        var tenant = new FixedTenantContext(tenantId);
        return new((DbContextOptions<ExtrasDbContext>)Configure(new DbContextOptionsBuilder<ExtrasDbContext>(), connectionString)
            .AddInterceptors(new TenantCommandInterceptor(tenant)).Options, tenant);
    }

    private static async Task<Results<Ok<List<MyExtrasResponse>>, ForbidHttpResult>> MyExtrasAsync(
        IUnitAvailability sales, ExtrasDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        var units = (await sales.ReservationsOfAsync(buyer, cancellationToken))
            .Where(r => r.State is nameof(Held) or nameof(Confirmed)).Select(r => r.UnitId).Distinct().ToList();
        var progress = await db.UnitProgress.AsNoTracking().Where(p => units.Contains(p.UnitId)).ToListAsync(cancellationToken);
        var developments = progress.Select(p => p.DevelopmentId).Distinct().ToList();
        var extras = await db.Extras.AsNoTracking().Where(e => developments.Contains(e.DevelopmentId)).OrderBy(e => e.Name).ToListAsync(cancellationToken);
        return TypedResults.Ok(progress.Select(p => new MyExtrasResponse(p.UnitId.Value, p.LatestMilestone?.ToString(),
            [.. extras.Where(e => e.DevelopmentId == p.DevelopmentId && e.Window.IsOpenAfter(p.LatestMilestone)).Select(ExtraResponse.From)])).ToList());
    }

    private static async Task<Ok<List<ExtraResponse>>> DevelopmentExtrasAsync(DevelopmentId developmentId, ExtrasDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.Extras.AsNoTracking().Where(e => e.DevelopmentId == developmentId).OrderBy(e => e.UnlocksAt).ThenBy(e => e.Name).ToListAsync(cancellationToken))
            .Select(ExtraResponse.From).ToList());
}

internal sealed record ExtraResponse(Guid Id, string Name, string Category, decimal Price, string Currency, string UnlocksAt, string ClosesAt, string? ConflictGroup, int MaxQuantity)
{
    public static ExtraResponse From(ExtraRow e) =>
        new(e.Id, e.Name, e.Category, e.Price, e.Currency, e.UnlocksAt.ToString(), e.ClosesAt.ToString(), e.ConflictGroup, e.MaxQuantity);
}

/// <param name="LatestMilestone">The latest milestone Extras has been told about for the unit (its own copy).</param>
internal sealed record MyExtrasResponse(Guid UnitId, string? LatestMilestone, IReadOnlyList<ExtraResponse> OpenExtras);

/// <summary>An extra in a development's catalogue.</summary>
internal sealed class ExtraRow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required DevelopmentId DevelopmentId { get; init; }

    public required string Name { get; set; }

    /// <summary>kitchen, flooring, smart-home, solar, landscaping, parking.</summary>
    public required string Category { get; set; }

    public required decimal Price { get; set; }

    public required string Currency { get; set; }

    public required Milestone UnlocksAt { get; set; }

    public required Milestone ClosesAt { get; set; }

    /// <summary>Extras in the same group exclude each other (two kitchen upgrades); null: no conflict.</summary>
    public string? ConflictGroup { get; set; }

    /// <summary>How many a buyer may order (a parking space: up to 2); 1 for most extras.</summary>
    public int MaxQuantity { get; set; } = 1;

    public ExtraWindow Window => new(UnlocksAt, ClosesAt);
}

/// <summary>Extras' own copy of a unit's build progress: only what the rules need (the latest milestone and its version).</summary>
internal sealed class UnitProgressRow
{
    public required UnitId UnitId { get; init; }

    public required DevelopmentId DevelopmentId { get; set; }

    public Milestone? LatestMilestone { get; set; }

    /// <summary>The BuildProgress version of the last event applied (C9 Lab 5): an older or repeated event changes nothing.</summary>
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ExtrasDbContext(DbContextOptions<ExtrasDbContext> options, ITenantContext tenant) : DbContext(options), ITenantScoped
{
    public const string Schema = "extras";

    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public DbSet<ExtraRow> Extras => Set<ExtraRow>();

    public DbSet<UnitProgressRow> UnitProgress => Set<UnitProgressRow>();

    public DbSet<BasketRow> Baskets => Set<BasketRow>();

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
        modelBuilder.Entity<ExtraRow>(extra =>
        {
            extra.ToTable("extras");
            extra.HasKey(e => e.Id);
            extra.HasIndex(e => e.DevelopmentId);
            extra.Property(e => e.Name).HasMaxLength(100);
            extra.Property(e => e.Category).HasMaxLength(40);
            extra.Property(e => e.Price).HasPrecision(12, 2);
            extra.Property(e => e.Currency).HasMaxLength(3).IsFixedLength();
            extra.Property(e => e.UnlocksAt).HasConversion<string>().HasMaxLength(40);
            extra.Property(e => e.ClosesAt).HasConversion<string>().HasMaxLength(40);
            extra.Property(e => e.ConflictGroup).HasMaxLength(40);
            extra.Ignore(e => e.Window);
        });
        modelBuilder.Entity<UnitProgressRow>(progress =>
        {
            progress.ToTable("unit_progress");
            progress.HasKey(p => p.UnitId);
            progress.Property(p => p.LatestMilestone).HasConversion<string>().HasMaxLength(40);
        });
        modelBuilder.Entity<BasketRow>(basket =>
        {
            basket.ToTable("baskets");
            basket.HasKey(b => b.Id);
            basket.Property(b => b.Id).ValueGeneratedNever(); // set in code (Version 7 GUIDs)
            basket.HasIndex(b => new { b.BuyerId, b.UnitId });
            basket.HasIndex(b => b.SessionId).IsUnique().HasFilter("session_id IS NOT NULL");
            basket.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
            basket.Property(b => b.SessionId).HasMaxLength(255);
            basket.Property(b => b.PaymentIntent).HasMaxLength(255);
            basket.Property(b => b.ChargedAmount).HasPrecision(12, 2);
            basket.HasMany(b => b.Lines).WithOne().HasForeignKey(l => l.BasketId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<BasketLineRow>(line =>
        {
            line.ToTable("basket_lines");
            line.HasKey(l => l.Id);
            // Set in code: without this, a new line added to a tracked basket has a key, so EF takes it for an
            // existing row and updates it (DbUpdateConcurrencyException: expected 1 row, affected 0).
            line.Property(l => l.Id).ValueGeneratedNever();
            line.Property(l => l.Name).HasMaxLength(100);
            line.Property(l => l.UnitPrice).HasPrecision(12, 2);
            line.Property(l => l.Currency).HasMaxLength(3).IsFixedLength();
            line.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
            line.Property(l => l.RefundReason).HasMaxLength(500);
            line.Property(l => l.RefundReference).HasMaxLength(255);
            line.Ignore(l => l.Amount);
        });
        modelBuilder.Entity<ExtraRow>().HasTenant(this);
        modelBuilder.Entity<UnitProgressRow>().HasTenant(this);
        modelBuilder.Entity<BasketRow>().HasTenant(this);
        modelBuilder.Entity<BasketLineRow>().HasTenant(this);
        modelBuilder.HasOutbox();
    }

    /// <summary>The demo tenant's catalogue: the same six kinds of extra in both developments, windows set per extra.</summary>
    internal static async Task SeedAsync(ExtrasDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Extras.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        foreach (var development in new[] { SeedData.Riverside, SeedData.Hillcrest })
        {
            ExtraRow Extra(string name, string category, decimal price, Milestone unlocks, Milestone closes, string? conflict = null, int max = 1) => new()
            {
                DevelopmentId = development, Name = name, Category = category, Price = price, Currency = "GBP",
                UnlocksAt = unlocks, ClosesAt = closes, ConflictGroup = conflict, MaxQuantity = max,
            };
            ExtraRow[] extras =
            [
                Extra("Premium kitchen", "kitchen", 6_500m, Milestone.Frame, Milestone.FirstFix, conflict: "kitchen"),
                Extra("Designer kitchen", "kitchen", 11_000m, Milestone.Frame, Milestone.FirstFix, conflict: "kitchen"),
                Extra("Engineered oak flooring", "flooring", 3_200m, Milestone.Roof, Milestone.Plastering),
                Extra("Smart home package", "smart-home", 2_400m, Milestone.Frame, Milestone.FirstFix),
                Extra("Solar panels", "solar", 5_800m, Milestone.Foundations, Milestone.Roof),
                Extra("Landscaped garden", "landscaping", 4_100m, Milestone.Plastering, Milestone.PracticalCompletion),
                Extra("Extra parking space", "parking", 9_000m, Milestone.Foundations, Milestone.SecondFix, max: 2),
            ];
            foreach (var extra in extras)
            {
                db.Entry(db.Extras.Add(extra).Entity).Property(TenantModel.TenantId).CurrentValue = SeedData.DemoTenant;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Consumes MilestoneCompleted from homebuild-extras. In one transaction: lock the unit's progress row, ignore the event
/// unless its version is newer than the one applied (an older, repeated or out-of-order event changes nothing), move
/// the unit's latest milestone, and write ExtrasUnlocked to the outbox for extras that were closed and are now open.
/// </summary>
internal sealed class ExtrasProgressConsumer(ExtrasDbContext db, RequestTenantContext tenant, TimeProvider time) : IMessageHandler
{
    public const string Name = "extras";

    public async Task HandleAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != IntegrationEvents.MilestoneCompleted)
        {
            return; // not ours (the filter policy should not send it): deleted
        }

        MilestoneCompletedMessage completed;
        try
        {
            completed = JsonSerializer.Deserialize<MilestoneCompletedMessage>(message.Body, JsonSerializerOptions.Web)
                ?? throw new PermanentMessageException($"Message {message.ProviderId} has no body.");
        }
        catch (JsonException e)
        {
            throw new PermanentMessageException($"Message {message.ProviderId} is not a MilestoneCompleted payload.", e);
        }

        if (completed.DevelopmentId is not { } developmentGuid || !Enum.TryParse<Milestone>(completed.Milestone, out var milestone))
        {
            throw new PermanentMessageException($"Message {message.ProviderId} has no development or an unknown milestone ({completed.Milestone}).");
        }

        tenant.TenantId = completed.TenantId; // row-level security: this tenant's catalogue and progress only
        var unitId = new UnitId(completed.UnitId);
        var developmentId = new DevelopmentId(developmentGuid);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Create the row if this is the unit's first event, then lock it: two events for one unit apply one at a time.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO " + ExtrasDbContext.Schema + ".unit_progress (unit_id, development_id, latest_milestone, version, updated_at, tenant_id) " +
                "VALUES ({0}, {1}, NULL, 0, {2}, {3}) ON CONFLICT (unit_id) DO NOTHING",
                [unitId.Value, developmentId.Value, time.GetUtcNow(), completed.TenantId], ct);
            var progress = await db.UnitProgress.FromSqlRaw("SELECT * FROM " + ExtrasDbContext.Schema + ".unit_progress WHERE unit_id = {0} FOR UPDATE", unitId.Value)
                .SingleAsync(ct);
            if (completed.Version <= progress.Version)
            {
                await transaction.CommitAsync(ct);
                return; // older or already applied
            }

            var before = progress.LatestMilestone;
            progress.LatestMilestone = milestone;
            progress.Version = completed.Version;
            progress.DevelopmentId = developmentId;
            progress.UpdatedAt = time.GetUtcNow();

            var opened = (await db.Extras.AsNoTracking().Where(e => e.DevelopmentId == developmentId).ToListAsync(ct))
                .Where(e => !e.Window.IsOpenAfter(before) && e.Window.IsOpenAfter(milestone))
                .OrderBy(e => e.Name)
                .Select(e => new UnlockedExtra(e.Id, e.Name, e.Price, e.Currency, e.ClosesAt.ToString()))
                .ToList();
            if (opened.Count > 0)
            {
                db.Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.ExtrasUnlocked,
                    new ExtrasUnlockedMessage(unitId.Value, completed.TenantId, developmentId.Value, milestone.ToString(), opened, time.GetUtcNow(), completed.Version),
                    completed.TenantId, time.GetUtcNow()));
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }
}
