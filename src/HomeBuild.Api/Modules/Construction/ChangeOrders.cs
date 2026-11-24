using System.Text.Json;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeBuild.Api.Modules.Construction;

public enum ChangeOrderStatus
{
    /// <summary>Created from a paid extra: on the site manager's board. The buyer sees "Ordered on site".</summary>
    Created,

    /// <summary>The site manager has seen it and planned it in. The buyer still sees "Ordered on site".</summary>
    Acknowledged,

    /// <summary>Fitted on site. The buyer sees "Fitted".</summary>
    Fitted,
}

/// <summary>
/// C10 Lab 3: work for the site manager from a paid extra: what (the extra and quantity), which unit, and by which
/// milestone it must be fitted (the extra's close milestone). One per paid basket line.
/// </summary>
internal sealed class ChangeOrderRow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BasketLineId { get; init; }

    public required UnitId UnitId { get; init; }

    public required DevelopmentId DevelopmentId { get; init; }

    public required Guid ExtraId { get; init; }

    public required string ExtraName { get; init; }

    public required int Quantity { get; init; }

    public required Milestone FitBy { get; init; }

    public ChangeOrderStatus Status { get; set; } = ChangeOrderStatus.Created;

    public required DateTimeOffset CreatedAt { get; init; }

    public string? AcknowledgedBy { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }

    public DateTimeOffset? FittedAt { get; set; }
}

/// <summary>Construction's published read for Extras' My Home view (C10 Lab 3): each paid line's change order, if one exists yet.</summary>
public interface IChangeOrderStatus
{
    Task<IReadOnlyDictionary<Guid, ChangeOrderStatus>> ForBasketLinesAsync(IReadOnlyCollection<Guid> basketLineIds, CancellationToken cancellationToken = default);
}

internal sealed class EfChangeOrderStatus(ConstructionDbContext db) : IChangeOrderStatus
{
    public async Task<IReadOnlyDictionary<Guid, ChangeOrderStatus>> ForBasketLinesAsync(IReadOnlyCollection<Guid> basketLineIds, CancellationToken cancellationToken = default) =>
        await db.ChangeOrders.AsNoTracking().Where(c => basketLineIds.Contains(c.BasketLineId)).ToDictionaryAsync(c => c.BasketLineId, c => c.Status, cancellationToken);
}

/// <summary>
/// C10 Lab 3: the change-order worker, a consumer of ExtrasPaid on homebuild-change-orders (in the API host: it writes
/// Construction's tables). One change order per paid line, keyed by the line (a unique index), so a redelivered or
/// relayed-twice ExtrasPaid creates nothing new; each new one leaves as ChangeOrderCreated through Construction's outbox,
/// in the same transaction.
/// </summary>
internal sealed class ChangeOrdersConsumer(ConstructionDbContext db, RequestTenantContext tenant, TimeProvider time) : IMessageHandler
{
    public const string Name = "change-orders";

    public async Task HandleAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != IntegrationEvents.ExtrasPaid)
        {
            return;
        }

        ExtrasPaidMessage paid;
        try
        {
            paid = JsonSerializer.Deserialize<ExtrasPaidMessage>(message.Body, JsonSerializerOptions.Web) ?? throw new PermanentMessageException($"Message {message.ProviderId} has no body.");
        }
        catch (JsonException e)
        {
            throw new PermanentMessageException($"Message {message.ProviderId} is not an ExtrasPaid payload.", e);
        }

        tenant.TenantId = paid.TenantId; // row-level security
        var unitId = new UnitId(paid.UnitId);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var developmentId = await db.BuildProgress.AsNoTracking().Where(p => p.UnitId == unitId).Select(p => (DevelopmentId?)p.DevelopmentId).SingleOrDefaultAsync(ct)
                ?? throw new PermanentMessageException($"Unit {paid.UnitId} has no build progress.");
            var now = time.GetUtcNow();
            foreach (var line in paid.Extras)
            {
                var fitBy = Enum.TryParse<Milestone>(line.FitBy, out var m) ? m : Milestone.PracticalCompletion;
                var id = Guid.CreateVersion7();
                // The unique basket line is the idempotency key: a duplicate inserts nothing and writes no event.
                var inserted = await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO " + ConstructionDbContext.Schema + ".change_orders (id, basket_line_id, unit_id, development_id, extra_id, extra_name, quantity, fit_by, status, created_at, tenant_id) " +
                    "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, 'Created', {8}, {9}) ON CONFLICT (basket_line_id) DO NOTHING",
                    [id, line.LineId, unitId.Value, developmentId.Value, line.ExtraId, line.Name, line.Quantity, fitBy.ToString(), now, paid.TenantId], ct);
                if (inserted == 1)
                {
                    db.Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.ChangeOrderCreated,
                        new ChangeOrderCreatedMessage(id, line.LineId, paid.UnitId, paid.TenantId, line.Name, line.Quantity, fitBy.ToString(), now), paid.TenantId, now));
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }
}

/// <summary>The staff DTO: everything the site manager needs (buyers see only a state, through Extras).</summary>
internal sealed record ChangeOrderResponse(Guid Id, Guid UnitId, string ExtraName, int Quantity, string FitBy, string Status, DateTimeOffset CreatedAt, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, DateTimeOffset? FittedAt)
{
    public static ChangeOrderResponse From(ChangeOrderRow c) =>
        new(c.Id, c.UnitId.Value, c.ExtraName, c.Quantity, c.FitBy.ToString(), c.Status.ToString(), c.CreatedAt, c.AcknowledgedBy, c.AcknowledgedAt, c.FittedAt);
}

internal static class ChangeOrderServices
{
    /// <summary>The homebuild-change-orders consumer in the API host, when ChangeOrders:QueueUrl is set; pausable by name.</summary>
    public static IServiceCollection AddChangeOrderConsumer(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<ConsumerPauses>();
        if (configuration["ChangeOrders:QueueUrl"] is { Length: > 0 } queueUrl)
        {
            services.TryAddSingleton<Amazon.SQS.IAmazonSQS>(_ => new Amazon.SQS.AmazonSQSClient(Amazon.RegionEndpoint.EUWest2));
            var options = configuration.GetSection("ChangeOrders:Consumer").Get<ConsumerOptions>() ?? new ConsumerOptions { Processors = 2, VisibilityTimeout = TimeSpan.FromMinutes(1) };
            // AddSingleton, not AddHostedService: that adds through TryAddEnumerable, one per implementation type, so a second QueueConsumer in the host was dropped (C10 Lab 7).
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new QueueConsumer(ChangeOrdersConsumer.Name, new SqsQueueClient(sp.GetRequiredService<Amazon.SQS.IAmazonSQS>(), queueUrl),
                sp.GetRequiredService<IServiceScopeFactory>(), options, sp.GetRequiredService<ILogger<QueueConsumer>>(), sp.GetRequiredService<ConsumerPauses>(), sp.GetService<ConsumerReadiness>()));
        }

        return services;
    }

#if DEBUG
    /// <summary>
    /// Test-only (Debug builds, Development environment): pause or resume a consumer, so an end-to-end test can show a
    /// state before its event is processed. Compiled out of Release builds.
    /// </summary>
    public static void MapConsumerControl(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/dev/consumers/{name}/{action:regex(^(pause|resume)$)}", (string name, string action, ConsumerPauses pauses) =>
        {
            if (action == "pause")
            {
                pauses.Pause(name);
            }
            else
            {
                pauses.Resume(name);
            }

            return TypedResults.Ok(new { consumer = name, paused = pauses.IsPaused(name) });
        }).WithTags(ConstructionModule.Name).AllowAnonymous().WithMetadata(new TenantNotRequiredAttribute()).ExcludeFromDescription();
    }
#endif
}

internal static class ChangeOrderEndpoints
{
    public static void MapChangeOrders(this RouteGroupBuilder group)
    {
        group.MapGet("/developments/{developmentId}/change-orders", ListAsync).MapToApiVersion(ApiVersions.V2).WithName("ListChangeOrders")
            .WithSummary("A development's change orders from paid extras (site manager, admin; v2)").RequireAuthorization(Policies.CanViewBuildBoard);
        group.MapPost("/change-orders/{id:guid}/acknowledgement", (Guid id, ConstructionDbContext db, IAuthorizationService authz, TimeProvider time, HttpContext http, CancellationToken ct) =>
                MoveAsync(id, ChangeOrderStatus.Acknowledged, db, authz, time, http, ct))
            .MapToApiVersion(ApiVersions.V2).WithName("AcknowledgeChangeOrder")
            .WithSummary("Acknowledge a change order (a site manager assigned to its development, v2)").RequireAuthorization(Policies.CanWorkOnSite);
        group.MapPost("/change-orders/{id:guid}/fitting", (Guid id, ConstructionDbContext db, IAuthorizationService authz, TimeProvider time, HttpContext http, CancellationToken ct) =>
                MoveAsync(id, ChangeOrderStatus.Fitted, db, authz, time, http, ct))
            .MapToApiVersion(ApiVersions.V2).WithName("MarkChangeOrderFitted")
            .WithSummary("Record a change order as fitted (a site manager assigned to its development, v2)").RequireAuthorization(Policies.CanWorkOnSite);
    }

    private static async Task<Ok<List<ChangeOrderResponse>>> ListAsync(DevelopmentId developmentId, ConstructionDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok((await db.ChangeOrders.AsNoTracking().Where(c => c.DevelopmentId == developmentId).OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken))
            .Select(ChangeOrderResponse.From).ToList());

    /// <summary>Created → Acknowledged → Fitted, in that order; the site manager must be assigned to the unit's development.</summary>
    private static async Task<Results<Ok<ChangeOrderResponse>, NotFound, ProblemHttpResult>> MoveAsync(
        Guid id, ChangeOrderStatus to, ConstructionDbContext db, IAuthorizationService authorization, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (await db.ChangeOrders.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is not { } order)
        {
            return TypedResults.NotFound();
        }

        if (!(await authorization.AuthorizeAsync(http.User, order.DevelopmentId, SiteOperations.CompleteMilestone)).Succeeded)
        {
            return TypedResults.Problem(Problems.Business(http, "not-assigned", "Not assigned to this development", StatusCodes.Status403Forbidden,
                "Only a site manager assigned to the unit's development may do this."));
        }

        if (order.Status != to - 1)
        {
            return TypedResults.Problem(Problems.Business(http, "change-order-state", "Change order cannot move", StatusCodes.Status409Conflict,
                $"The change order is {order.Status}; it must be {to - 1} first."));
        }

        order.Status = to;
        if (to == ChangeOrderStatus.Acknowledged)
        {
            order.AcknowledgedBy = http.User.FindFirst("sub")?.Value;
            order.AcknowledgedAt = time.GetUtcNow();
        }
        else
        {
            order.FittedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ChangeOrderResponse.From(order));
    }
}
