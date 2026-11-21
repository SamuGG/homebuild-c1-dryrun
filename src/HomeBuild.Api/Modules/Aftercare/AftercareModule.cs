using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Aftercare;

public sealed class AftercareOptions
{
    public const string Section = "Aftercare";

    /// <summary>The defects period after handover, in months (default two years).</summary>
    [Range(1, 120)]
    public int DefectsPeriodMonths { get; set; } = 24;
}

/// <summary>
/// C10 Lab 4: Aftercare. Handover (a checklist Aftercare completes; it starts the defects period and publishes the
/// handover pack to My Home) and snags (the two-party lifecycle of <see cref="SnagLifecycle"/>, with a timeline).
/// Buyers and Aftercare read different DTOs: Aftercare's internal notes never reach the buyer.
/// </summary>
public static class AftercareModule
{
    public const string Name = "Aftercare";

    public static IServiceCollection AddAftercareModule(this IServiceCollection services)
    {
        services.AddOptions<AftercareOptions>().BindConfiguration(AftercareOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddDbContext<AftercareDbContext>((sp, options) => Configure(options, HomeBuildDatabase.ConnectionString(sp))
            .AddInterceptors(new TenantCommandInterceptor(sp.GetRequiredService<ITenantContext>()), sp.GetRequiredService<OpenTransactions>()));
        return services;
    }

    public static IEndpointRouteBuilder MapAftercareModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name);
        group.MapHandover();
        group.MapSnags();
        return endpoints;
    }

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, AftercareDbContext.Schema);

    internal static AftercareDbContext CreateDbContext(string connectionString, Guid? tenantId = null)
    {
        var tenant = new FixedTenantContext(tenantId);
        return new((DbContextOptions<AftercareDbContext>)Configure(new DbContextOptionsBuilder<AftercareDbContext>(), connectionString)
            .AddInterceptors(new TenantCommandInterceptor(tenant)).Options, tenant);
    }
}

/// <summary>A unit's handover: the checklist Aftercare completes, and the defects period it starts.</summary>
internal sealed class HandoverRow
{
    public required UnitId UnitId { get; init; }

    public required BuyerId BuyerId { get; set; }

    public bool KeysHandedOver { get; set; }

    public string? MeterReadings { get; set; }

    public bool WarrantiesIssued { get; set; }

    public string? CompletedBy { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The last day of the defects period; null until the handover is complete.</summary>
    public DateOnly? DefectsEndOn { get; set; }
}

internal sealed class AftercareDbContext(DbContextOptions<AftercareDbContext> options, ITenantContext tenant) : DbContext(options), ITenantScoped
{
    public const string Schema = "aftercare";

    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public DbSet<HandoverRow> Handovers => Set<HandoverRow>();

    public DbSet<Snag> Snags => Set<Snag>();

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
        modelBuilder.Entity<HandoverRow>(handover =>
        {
            handover.ToTable("handovers");
            handover.HasKey(h => h.UnitId);
            handover.Property(h => h.MeterReadings).HasMaxLength(500);
            handover.Property(h => h.CompletedBy).HasMaxLength(100);
        });
        modelBuilder.Entity<Snag>(snag =>
        {
            snag.ToTable("snags");
            snag.HasKey(s => s.Id);
            snag.Property(s => s.Id).ValueGeneratedNever();
            snag.HasIndex(s => s.UnitId);
            snag.Property(s => s.Area).HasMaxLength(100);
            snag.Property(s => s.Description).HasMaxLength(2000);
            snag.Property(s => s.Severity).HasConversion<string>().HasMaxLength(20);
            snag.Property(s => s.State).HasConversion(new SnagStateConverter()).HasMaxLength(500);
            snag.OwnsMany(s => s.Timeline, entry =>
            {
                entry.ToTable("snag_timeline");
                entry.WithOwner().HasForeignKey("SnagId");
                entry.HasKey(e => e.Id);
                entry.Property(e => e.Id).ValueGeneratedNever();
                entry.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20);
                entry.Property(e => e.Actor).HasConversion<string>().HasMaxLength(20);
                entry.Property(e => e.ActorId).HasMaxLength(100);
                entry.Property(e => e.Text).HasMaxLength(2000);
                entry.Property(e => e.From).HasMaxLength(20);
                entry.Property(e => e.To).HasMaxLength(20);
                entry.Ignore(e => e.IsInternal);
            });
            snag.Navigation(s => s.Timeline).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<HandoverRow>().HasTenant(this);
        modelBuilder.Entity<Snag>().HasTenant(this);
        modelBuilder.HasOutbox(); // HandoverCompleted
    }
}

/// <summary>A snag's state as text: the state's name, and JSON for the states that carry data.</summary>
internal sealed class SnagStateConverter() : ValueConverter<SnagState, string>(state => ToText(state), text => FromText(text))
{
    private static string ToText(SnagState state) => state switch
    {
        SnagState.Scheduled s => "Scheduled|" + JsonSerializer.Serialize(new[] { s.Subcontractor, s.VisitOn.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }),
        SnagState.Disputed d => "Disputed|" + JsonSerializer.Serialize(new[] { d.Reason }),
        _ => state.Name,
    };

    private static SnagState FromText(string text)
    {
        var name = text.Split('|')[0];
        var data = text.Contains('|', StringComparison.Ordinal) ? JsonSerializer.Deserialize<string[]>(text[(name.Length + 1)..])! : [];
        return name switch
        {
            nameof(SnagState.Draft) => new SnagState.Draft(),
            nameof(SnagState.Reported) => new SnagState.Reported(),
            nameof(SnagState.Acknowledged) => new SnagState.Acknowledged(),
            nameof(SnagState.Scheduled) => new SnagState.Scheduled(data[0], DateOnly.Parse(data[1], System.Globalization.CultureInfo.InvariantCulture)),
            nameof(SnagState.Fixed) => new SnagState.Fixed(),
            nameof(SnagState.Confirmed) => new SnagState.Confirmed(),
            nameof(SnagState.Disputed) => new SnagState.Disputed(data[0]),
            nameof(SnagState.Closed) => new SnagState.Closed(),
            _ => throw new InvalidOperationException($"Unknown snag state '{name}'."),
        };
    }
}

internal sealed record HandoverRequest(bool KeysHandedOver, [property: StringLength(500)] string? MeterReadings, bool WarrantiesIssued);

/// <summary>The handover pack in My Home: what was handed over, and until when defects are covered.</summary>
internal sealed record HandoverPackResponse(Guid UnitId, bool KeysHandedOver, string? MeterReadings, bool WarrantiesIssued, DateTimeOffset? CompletedAt, DateOnly? DefectsEndOn, IReadOnlyList<string> Documents);

internal static class HandoverEndpoints
{
    private static readonly string[] Documents = ["Home warranty certificate", "Appliance manuals and warranties", "Meter readings at handover", "Building regulations completion certificate"];

    public static void MapHandover(this RouteGroupBuilder group)
    {
        group.MapPut("/units/{unitId}/handover", RecordAsync).MapToApiVersion(ApiVersions.V2).WithName("RecordHandover")
            .WithSummary("Record the handover checklist; when it is complete, the defects period starts (Aftercare, v2)")
            .ProducesProblem(StatusCodes.Status409Conflict).RequireAuthorization(Policies.CanManageAftercare);
        group.MapGet("/me/handover", PacksAsync).MapToApiVersion(ApiVersions.V2).WithName("MyHandoverPacks")
            .WithSummary("The handover packs of the buyer's homes (v2)").RequireAuthorization(Policies.CanReadOwnProgress);
    }

    /// <summary>
    /// Saves the checklist; once keys, meter readings and warranties are all done, the handover completes: the defects
    /// period starts (handover date + Aftercare:DefectsPeriodMonths) and HandoverCompleted goes through the outbox.
    /// </summary>
    private static async Task<Results<Ok<HandoverPackResponse>, NotFound, ProblemHttpResult>> RecordAsync(
        UnitId unitId, HandoverRequest request, AftercareDbContext db, IUnitAvailability sales, IReservationReader reservations, IOptions<AftercareOptions> options, TimeProvider time,
        HttpContext http, CancellationToken cancellationToken)
    {
        if (await sales.LatestReservationAsync(unitId, cancellationToken) is not { Status: "confirmed" } latest
            || await reservations.FindAsync(latest.ReservationId, cancellationToken) is not { } reservation)
        {
            return TypedResults.Problem(Problems.Business(http, "not-sold", "No buyer to hand over to", StatusCodes.Status409Conflict,
                "A home is handed over to the buyer of a confirmed reservation."));
        }

        var handover = await db.Handovers.SingleOrDefaultAsync(h => h.UnitId == unitId, cancellationToken);
        if (handover?.CompletedAt is not null)
        {
            return TypedResults.Problem(Problems.Business(http, "handover-complete", "Already handed over", StatusCodes.Status409Conflict, "The handover is complete."));
        }

        if (handover is null)
        {
            handover = new HandoverRow { UnitId = unitId, BuyerId = reservation.BuyerId };
            db.Handovers.Add(handover);
        }

        handover.KeysHandedOver = request.KeysHandedOver;
        handover.MeterReadings = request.MeterReadings;
        handover.WarrantiesIssued = request.WarrantiesIssued;
        if (request is { KeysHandedOver: true, MeterReadings.Length: > 0, WarrantiesIssued: true })
        {
            var now = time.GetUtcNow();
            handover.CompletedAt = now;
            handover.CompletedBy = http.User.FindFirst("sub")?.Value;
            handover.DefectsEndOn = DateOnly.FromDateTime(now.UtcDateTime).AddMonths(options.Value.DefectsPeriodMonths).AddDays(-1);
            db.Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.HandoverCompleted,
                new HandoverCompletedMessage(unitId.Value, db.CurrentTenantId, reservation.BuyerId.Value, now, handover.DefectsEndOn.Value), db.CurrentTenantId, now));
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(Pack(handover));
    }

    private static async Task<Results<Ok<List<HandoverPackResponse>>, ForbidHttpResult>> PacksAsync(AftercareDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        return TypedResults.Ok((await db.Handovers.AsNoTracking().Where(h => h.BuyerId == buyer && h.CompletedAt != null).ToListAsync(cancellationToken)).Select(Pack).ToList());
    }

    private static HandoverPackResponse Pack(HandoverRow h) =>
        new(h.UnitId.Value, h.KeysHandedOver, h.MeterReadings, h.WarrantiesIssued, h.CompletedAt, h.DefectsEndOn, h.CompletedAt is null ? [] : Documents);
}

internal sealed record NewSnagRequest(Guid UnitId, [property: Required, StringLength(100, MinimumLength = 1)] string Area,
    [property: Required, StringLength(2000, MinimumLength = 1)] string Description, [property: Required, RegularExpression("^(Minor|Major|Urgent)$")] string Severity);

internal sealed record SnagActionRequest([property: StringLength(100)] string? Subcontractor, DateOnly? VisitOn, [property: StringLength(1000)] string? Reason);

internal sealed record SnagTextRequest([property: Required, StringLength(2000, MinimumLength = 1)] string Text);

internal sealed record SnagEntryResponse(string Kind, string Actor, DateTimeOffset At, string Text, string? From, string? To);

/// <summary>The buyer's DTO: no internal notes, no staff IDs.</summary>
internal sealed record BuyerSnagResponse(Guid Id, Guid UnitId, string Area, string Description, string Severity, string State, string? Subcontractor, DateOnly? VisitOn, IReadOnlyList<SnagEntryResponse> Timeline);

internal sealed record StaffSnagEntryResponse(string Kind, string Actor, string ActorId, DateTimeOffset At, string Text, string? From, string? To, bool Internal);

/// <summary>Aftercare's DTO: the whole timeline, internal notes and who did what included.</summary>
internal sealed record StaffSnagResponse(Guid Id, Guid UnitId, string Area, string Description, string Severity, string State, string? Subcontractor, DateOnly? VisitOn, bool BeforeHandover, IReadOnlyList<StaffSnagEntryResponse> Timeline);

internal static class SnagEndpoints
{
    public static void MapSnags(this RouteGroupBuilder group)
    {
        group.MapPost("/me/snags", CreateAsync).MapToApiVersion(ApiVersions.V2).WithName("DraftSnag")
            .WithSummary("Draft a snag on one of the buyer's homes (v2)").RequireAuthorization(Policies.CanReadOwnProgress);
        group.MapGet("/me/snags", MineAsync).MapToApiVersion(ApiVersions.V2).WithName("MySnags")
            .WithSummary("The buyer's snags with their timelines, without Aftercare's internal notes (v2)").RequireAuthorization(Policies.CanReadOwnProgress);
        group.MapPost("/units/{unitId}/snags", RecordAsync).MapToApiVersion(ApiVersions.V2).WithName("RecordSnagBeforeHandover")
            .WithSummary("Record a snag before handover (Aftercare, v2)").RequireAuthorization(Policies.CanManageAftercare);
        group.MapGet("/snags", ListAsync).MapToApiVersion(ApiVersions.V2).WithName("ListSnags")
            .WithSummary("Every snag with its full timeline (Aftercare, v2)").RequireAuthorization(Policies.CanManageAftercare);
        group.MapPost("/snags/{id:guid}/{action}", ActAsync).MapToApiVersion(ApiVersions.V2).WithName("ActOnSnag")
            .WithSummary("Move a snag: report, confirm, dispute (buyer); acknowledge, schedule, fix, close (Aftercare) (v2)")
            .ProducesProblem(StatusCodes.Status409Conflict).RequireAuthorization(Policies.CanActOnSnags);
        group.MapPost("/snags/{id:guid}/comments", CommentAsync).MapToApiVersion(ApiVersions.V2).WithName("CommentOnSnag")
            .WithSummary("Comment on a snag (buyer or Aftercare, v2)").RequireAuthorization(Policies.CanActOnSnags);
        group.MapPost("/snags/{id:guid}/notes", NoteAsync).MapToApiVersion(ApiVersions.V2).WithName("NoteOnSnag")
            .WithSummary("An internal note the buyer never sees (Aftercare, v2)").RequireAuthorization(Policies.CanManageAftercare);
    }

    private static async Task<Results<Created<BuyerSnagResponse>, NotFound, ForbidHttpResult>> CreateAsync(
        NewSnagRequest request, AftercareDbContext db, IUnitAvailability sales, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        var unitId = new UnitId(request.UnitId);
        if (!await OwnsAsync(sales, buyer, unitId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var snag = Snag.Draft(unitId, request.Area, request.Description, Enum.Parse<SnagSeverity>(request.Severity), buyer.Value.ToString(), time.GetUtcNow());
        db.Snags.Add(snag);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v2/me/snags/{snag.Id}", ForBuyer(snag));
    }

    private static async Task<Results<Created<StaffSnagResponse>, ProblemHttpResult>> RecordAsync(
        UnitId unitId, NewSnagRequest request, AftercareDbContext db, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        var snag = Snag.RecordedBeforeHandover(unitId, request.Area, request.Description, Enum.Parse<SnagSeverity>(request.Severity), http.User.FindFirst("sub")?.Value ?? "staff", time.GetUtcNow());
        db.Snags.Add(snag);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v2/snags/{snag.Id}", ForStaff(snag));
    }

    private static async Task<Results<Ok<List<BuyerSnagResponse>>, ForbidHttpResult>> MineAsync(AftercareDbContext db, IUnitAvailability sales, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        var units = (await sales.ReservationsOfAsync(buyer, cancellationToken)).Where(r => r.State is nameof(Held) or nameof(Confirmed)).Select(r => r.UnitId).ToList();
        var snags = await db.Snags.AsNoTracking().Where(s => units.Contains(s.UnitId)).OrderByDescending(s => s.CreatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(snags.Select(ForBuyer).ToList());
    }

    private static async Task<Ok<List<StaffSnagResponse>>> ListAsync(Guid? unitId, AftercareDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.Snags.AsNoTracking().Where(s => unitId == null || s.UnitId == new UnitId(unitId.Value)).OrderByDescending(s => s.CreatedAt).ToListAsync(cancellationToken))
            .Select(ForStaff).ToList());

    /// <summary>The actor comes from the caller's role; a buyer must own the snag's home. The lifecycle table decides the rest.</summary>
    private static async Task<Results<Ok<BuyerSnagResponse>, Ok<StaffSnagResponse>, NotFound, ProblemHttpResult>> ActAsync(
        Guid id, string action, SnagActionRequest? request, AftercareDbContext db, IUnitAvailability sales, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SnagAction>(action, ignoreCase: true, out var snagAction) || await LoadAsync(db, sales, id, http, cancellationToken) is not { } loaded)
        {
            return TypedResults.NotFound();
        }

        var (snag, actor, actorId) = loaded;
        var result = snag.Act(snagAction, actor, actorId, new SnagActionDetails(request?.Subcontractor, request?.VisitOn, request?.Reason), time.GetUtcNow());
        if (result.Value is SnagRefused refused)
        {
            return TypedResults.Problem(Problems.Business(http, "snag-transition", "Not allowed", StatusCodes.Status409Conflict, refused.Reason));
        }

        await db.SaveChangesAsync(cancellationToken);
        return actor == SnagActor.Buyer ? TypedResults.Ok(ForBuyer(snag)) : TypedResults.Ok(ForStaff(snag));
    }

    private static async Task<Results<NoContent, NotFound>> CommentAsync(Guid id, SnagTextRequest request, AftercareDbContext db, IUnitAvailability sales, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (await LoadAsync(db, sales, id, http, cancellationToken) is not { } loaded)
        {
            return TypedResults.NotFound();
        }

        loaded.Snag.Comment(loaded.Actor, loaded.ActorId, request.Text, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> NoteAsync(Guid id, SnagTextRequest request, AftercareDbContext db, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (await db.Snags.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } snag)
        {
            return TypedResults.NotFound();
        }

        snag.InternalNote(http.User.FindFirst("sub")?.Value ?? "staff", request.Text, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<(Snag Snag, SnagActor Actor, string ActorId)?> LoadAsync(AftercareDbContext db, IUnitAvailability sales, Guid id, HttpContext http, CancellationToken cancellationToken)
    {
        if (await db.Snags.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } snag)
        {
            return null;
        }

        if (http.User.IsInRole(Roles.Aftercare))
        {
            return (snag, SnagActor.Aftercare, http.User.FindFirst("sub")?.Value ?? "staff");
        }

        // A buyer may act only on their own home's snags (BOLA, C4): anyone else's is "not found".
        return http.User.CallerBuyerId() is { } buyer && await OwnsAsync(sales, buyer, snag.UnitId, cancellationToken)
            ? (snag, SnagActor.Buyer, buyer.Value.ToString())
            : null;
    }

    private static async Task<bool> OwnsAsync(IUnitAvailability sales, BuyerId buyer, UnitId unitId, CancellationToken cancellationToken) =>
        (await sales.ReservationsOfAsync(buyer, cancellationToken)).Any(r => r.UnitId == unitId && r.State is nameof(Held) or nameof(Confirmed));

    private static BuyerSnagResponse ForBuyer(Snag s) => new(s.Id, s.UnitId.Value, s.Area, s.Description, s.Severity.ToString(), s.State.Name,
        (s.State as SnagState.Scheduled)?.Subcontractor, (s.State as SnagState.Scheduled)?.VisitOn,
        [.. s.Timeline.Where(e => !e.IsInternal).OrderBy(e => e.At).Select(e => new SnagEntryResponse(e.Kind.ToString(), e.Actor.ToString(), e.At, e.Text, e.From, e.To))]);

    private static StaffSnagResponse ForStaff(Snag s) => new(s.Id, s.UnitId.Value, s.Area, s.Description, s.Severity.ToString(), s.State.Name,
        (s.State as SnagState.Scheduled)?.Subcontractor, (s.State as SnagState.Scheduled)?.VisitOn, s.BeforeHandover,
        [.. s.Timeline.OrderBy(e => e.At).Select(e => new StaffSnagEntryResponse(e.Kind.ToString(), e.Actor.ToString(), e.ActorId, e.At, e.Text, e.From, e.To, e.IsInternal))]);
}
