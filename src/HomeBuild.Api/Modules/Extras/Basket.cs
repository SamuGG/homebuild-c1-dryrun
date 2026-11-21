using System.Data.Common;
using HomeBuild.Api.Http;
using HomeBuild.Api.Http.Idempotency;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using HomeBuild.Payments;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Extras;

public enum BasketStatus
{
    /// <summary>The buyer is choosing.</summary>
    Open,

    /// <summary>A Checkout page is open: the basket is frozen until Stripe says paid or expired. My Home: "Confirming".</summary>
    CheckingOut,

    /// <summary>The payment webhook was processed: an order (its refunded lines, if any, say why).</summary>
    Paid,
}

public enum BasketLineStatus
{
    InBasket,
    Paid,

    /// <summary>Failed revalidation at payment: to be refunded after the commit.</summary>
    RefundPending,
    Refunded,
}

/// <summary>C10 Lab 2: one buyer's basket of extras for one home; after payment, the order.</summary>
internal sealed class BasketRow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required BuyerId BuyerId { get; init; }

    public required UnitId UnitId { get; init; }

    public required DevelopmentId DevelopmentId { get; init; }

    public BasketStatus Status { get; set; } = BasketStatus.Open;

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Increments with each Checkout page, so each attempt has its own Stripe idempotency key.</summary>
    public int CheckoutAttempts { get; set; }

    public string? SessionId { get; set; }

    public string? PaymentIntent { get; set; }

    /// <summary>What Stripe charged (the session's amount_total), recorded for reconciliation against the lines.</summary>
    public decimal? ChargedAmount { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public List<BasketLineRow> Lines { get; init; } = [];
}

/// <summary>A line with its snapshot: the extra's name and price when the buyer added it. Orders use these, never the catalogue.</summary>
internal sealed class BasketLineRow
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid BasketId { get; init; }

    public required Guid ExtraId { get; init; }

    public required string Name { get; init; }

    public required decimal UnitPrice { get; init; }

    public required string Currency { get; init; }

    public int Quantity { get; set; } = 1;

    public required DateTimeOffset AddedAt { get; init; }

    public BasketLineStatus Status { get; set; } = BasketLineStatus.InBasket;

    public string? RefundReason { get; set; }

    public string? RefundReference { get; set; }

    public decimal Amount => UnitPrice * Quantity;
}

/// <summary>
/// C10 Lab 2's rules, checked when an item is added, when the Checkout page is created and again when the payment
/// arrives: the extra is still offered and open for the unit, the quantity is within its limit (counting what the buyer
/// has already bought for the home), and it does not conflict with another extra in the basket or already bought.
/// </summary>
internal static class BasketRules
{
    /// <summary>Each line and why it fails now (null: it passes), in the order the buyer added them.</summary>
    public static List<(BasketLineRow Line, string? Problem)> Check(
        IEnumerable<BasketLineRow> lines, IReadOnlyDictionary<Guid, ExtraRow> extras, Milestone? latestMilestone, IReadOnlyCollection<BasketLineRow> alreadyBought)
    {
        var accepted = new List<BasketLineRow>(alreadyBought);
        var results = new List<(BasketLineRow, string?)>();
        foreach (var line in lines.OrderBy(l => l.AddedAt))
        {
            var problem = ProblemOf(line, extras, latestMilestone, accepted);
            if (problem is null)
            {
                accepted.Add(line);
            }

            results.Add((line, problem));
        }

        return results;
    }

    private static string? ProblemOf(BasketLineRow line, IReadOnlyDictionary<Guid, ExtraRow> extras, Milestone? latest, List<BasketLineRow> accepted)
    {
        if (!extras.TryGetValue(line.ExtraId, out var extra))
        {
            return $"{line.Name} is no longer offered.";
        }

        if (!extra.Window.IsOpenAfter(latest))
        {
            return latest is { } m && m >= extra.ClosesAt
                ? $"{extra.Name} closed when {extra.ClosesAt} was completed."
                : $"{extra.Name} is not open yet (it opens when {extra.UnlocksAt} is completed).";
        }

        var quantity = accepted.Where(a => a.ExtraId == extra.Id).Sum(a => a.Quantity) + line.Quantity;
        if (quantity > extra.MaxQuantity)
        {
            return $"{extra.Name}: at most {extra.MaxQuantity} per home.";
        }

        if (extra.ConflictGroup is { } group
            && accepted.FirstOrDefault(a => a.ExtraId != extra.Id && extras.TryGetValue(a.ExtraId, out var other) && other.ConflictGroup == group) is { } clash)
        {
            return $"{extra.Name} cannot be combined with {clash.Name}.";
        }

        return null;
    }

    public static bool IsConflict(string problem) => problem.Contains("cannot be combined", StringComparison.Ordinal);
}

/// <param name="State">C10 Lab 3: what the buyer may be told, and only once the system has reached it: "In basket",
/// "Payment confirming", "Paid", "Ordered on site" (a change order exists), "Fitted", or "Refunded".</param>
internal sealed record BasketLineResponse(Guid Id, Guid ExtraId, string Name, decimal UnitPrice, string Currency, int Quantity, string Status, string? Reason, string State);

/// <param name="Status">open, confirming (a Checkout page is open or Stripe has not confirmed yet) or paid.</param>
internal sealed record BasketResponse(Guid Id, Guid UnitId, string Status, IReadOnlyList<BasketLineResponse> Lines, decimal Total, string Currency, DateTimeOffset? PaidAt)
{
    public static BasketResponse From(BasketRow b) => From(b, new Dictionary<Guid, HomeBuild.Api.Modules.Construction.ChangeOrderStatus>());

    public static BasketResponse From(BasketRow b, IReadOnlyDictionary<Guid, HomeBuild.Api.Modules.Construction.ChangeOrderStatus> changeOrders) => new(b.Id, b.UnitId.Value,
        b.Status switch { BasketStatus.Open => "open", BasketStatus.CheckingOut => "confirming", _ => "paid" },
        [.. b.Lines.OrderBy(l => l.AddedAt).Select(l => new BasketLineResponse(l.Id, l.ExtraId, l.Name, l.UnitPrice, l.Currency, l.Quantity,
            l.Status switch { BasketLineStatus.InBasket => "in-basket", BasketLineStatus.Paid => "paid", BasketLineStatus.RefundPending => "refund-pending", _ => "refunded" }, l.RefundReason,
            StateOf(b, l, changeOrders)))],
        b.Lines.Where(l => l.Status is BasketLineStatus.InBasket or BasketLineStatus.Paid).Sum(l => l.Amount),
        b.Lines.FirstOrDefault()?.Currency ?? "GBP", b.PaidAt);

    /// <summary>
    /// Each state comes from an event that has been processed: the payment webhook (Paid), the change-order worker
    /// (Ordered on site), the site manager (Fitted). Nothing is shown ahead of the system.
    /// </summary>
    private static string StateOf(BasketRow b, BasketLineRow l, IReadOnlyDictionary<Guid, HomeBuild.Api.Modules.Construction.ChangeOrderStatus> changeOrders) => (b.Status, l.Status) switch
    {
        (BasketStatus.Open, _) => "In basket",
        (BasketStatus.CheckingOut, _) => "Payment confirming",
        (_, BasketLineStatus.RefundPending or BasketLineStatus.Refunded) => "Refunded",
        _ when changeOrders.TryGetValue(l.Id, out var order) => order == HomeBuild.Api.Modules.Construction.ChangeOrderStatus.Fitted ? "Fitted" : "Ordered on site",
        _ => "Paid",
    };
}

internal sealed record AddExtraRequest(Guid UnitId, Guid ExtraId, int Quantity = 1);

internal sealed record BasketCheckoutStartRequest(Guid UnitId);

internal sealed record BasketCheckoutResponse(string SessionId, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>C10 Lab 2: the buyer's basket endpoints (v2).</summary>
internal static class BasketEndpoints
{
    public static void MapBasket(this RouteGroupBuilder group)
    {
        group.MapGet("/me/baskets", ListAsync).MapToApiVersion(ApiVersions.V2).WithName("ListMyBaskets")
            .WithSummary("The signed-in buyer's extras baskets and orders (v2)").RequireAuthorization(Policies.CanReadOwnProgress);
        group.MapPost("/me/basket/lines", AddAsync).MapToApiVersion(ApiVersions.V2).WithName("AddExtraToBasket")
            .WithSummary("Add an open extra to the basket for one of the buyer's homes (v2)")
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(Policies.CanReadOwnProgress);
        group.MapDelete("/me/basket/lines/{lineId:guid}", RemoveAsync).MapToApiVersion(ApiVersions.V2).WithName("RemoveExtraFromBasket")
            .WithSummary("Remove a line from an open basket (v2)").RequireAuthorization(Policies.CanReadOwnProgress);
        group.MapPost("/me/basket/checkout", CheckoutAsync).MapToApiVersion(ApiVersions.V2).WithName("StartBasketCheckout")
            .WithSummary("Revalidate the basket and open a Stripe Checkout page for it (v2)")
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireIdempotencyKey() // C8 Lab 3
            .RequireAuthorization(Policies.CanReadOwnProgress);
    }

    private static async Task<Results<Ok<List<BasketResponse>>, ForbidHttpResult>> ListAsync(
        ExtrasDbContext db, HomeBuild.Api.Modules.Construction.IChangeOrderStatus changeOrders, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        var baskets = (await db.Baskets.AsNoTracking().Include(b => b.Lines).Where(b => b.BuyerId == buyer).OrderByDescending(b => b.CreatedAt).ToListAsync(cancellationToken))
            .Where(b => b.Lines.Count > 0).ToList();
        // C10 Lab 3: Construction's published read says which paid lines have a change order yet.
        var orders = await changeOrders.ForBasketLinesAsync([.. baskets.Where(b => b.Status == BasketStatus.Paid).SelectMany(b => b.Lines).Select(l => l.Id)], cancellationToken);
        return TypedResults.Ok(baskets.Select(b => BasketResponse.From(b, orders)).ToList());
    }

    private static async Task<Results<Ok<BasketResponse>, NotFound, ForbidHttpResult, ProblemHttpResult>> AddAsync(
        AddExtraRequest request, ExtrasDbContext db, IUnitAvailability sales, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        var unitId = new UnitId(request.UnitId);
        if (!await HoldsAsync(sales, buyer, unitId, cancellationToken)
            || await db.UnitProgress.AsNoTracking().SingleOrDefaultAsync(p => p.UnitId == unitId, cancellationToken) is not { } progress
            || await db.Extras.AsNoTracking().SingleOrDefaultAsync(e => e.Id == request.ExtraId && e.DevelopmentId == progress.DevelopmentId, cancellationToken) is not { } extra)
        {
            return TypedResults.NotFound(); // not their home, or no such extra for it
        }

        if (request.Quantity is < 1 or > 10)
        {
            return TypedResults.Problem(Problems.Business(http, "extra-quantity", "Quantity out of range", StatusCodes.Status422UnprocessableEntity, "Choose between 1 and 10."));
        }

        var basket = await OpenBasketAsync(db, buyer, unitId, progress.DevelopmentId, time, cancellationToken);
        if (basket.Status != BasketStatus.Open)
        {
            return TypedResults.Problem(Problems.Business(http, "basket-checking-out", "Payment in progress", StatusCodes.Status409Conflict,
                "This basket has a payment page open. Pay or wait for it to expire, then change the basket."));
        }

        // The snapshot: the price and name now, kept on the line whatever the catalogue does later.
        var line = basket.Lines.FirstOrDefault(l => l.ExtraId == extra.Id);
        var candidate = new BasketLineRow { ExtraId = extra.Id, Name = extra.Name, UnitPrice = extra.Price, Currency = extra.Currency, Quantity = request.Quantity, AddedAt = time.GetUtcNow() };
        var proposed = basket.Lines.Where(l => l != line).Append(line is null ? candidate : new BasketLineRow
        {
            ExtraId = line.ExtraId, Name = line.Name, UnitPrice = line.UnitPrice, Currency = line.Currency, Quantity = line.Quantity + request.Quantity, AddedAt = line.AddedAt,
        }).ToList();
        var check = BasketRules.Check(proposed, await CatalogueAsync(db, progress.DevelopmentId, cancellationToken), progress.LatestMilestone, await BoughtAsync(db, unitId, basket.Id, cancellationToken));
        if (check.FirstOrDefault(c => c.Problem is not null).Problem is { } problem)
        {
            return TypedResults.Problem(BasketRules.IsConflict(problem)
                ? Problems.Business(http, "extra-conflict", "Extras conflict", StatusCodes.Status409Conflict, problem)
                : Problems.Business(http, "extra-unavailable", "Extra unavailable", StatusCodes.Status422UnprocessableEntity, problem));
        }

        if (line is null)
        {
            basket.Lines.Add(candidate);
        }
        else
        {
            line.Quantity += request.Quantity;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(BasketResponse.From(basket));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveAsync(Guid lineId, ExtrasDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        var buyer = http.User.CallerBuyerId();
        if (await db.Baskets.Include(b => b.Lines).SingleOrDefaultAsync(b => b.BuyerId == buyer && b.Lines.Any(l => l.Id == lineId), cancellationToken) is not { } basket)
        {
            return TypedResults.NotFound();
        }

        if (basket.Status != BasketStatus.Open)
        {
            return TypedResults.Problem(Problems.Business(http, "basket-checking-out", "Payment in progress", StatusCodes.Status409Conflict, "This basket can no longer be changed."));
        }

        basket.Lines.RemoveAll(l => l.Id == lineId);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Revalidates every line against the current state (open, limits, conflicts) and opens one Stripe Checkout page for
    /// the basket, at the snapshot prices. The basket is frozen until the webhook says paid or expired.
    /// </summary>
    private static async Task<Results<Ok<BasketCheckoutResponse>, NotFound, ForbidHttpResult, ProblemHttpResult>> CheckoutAsync(
        BasketCheckoutStartRequest request, ExtrasDbContext db, IUnitAvailability sales, IPaymentGateway gateway, ITenantContext tenant, TimeProvider time,
        Microsoft.Extensions.Options.IOptions<HomeBuild.Api.Configuration.PaymentsOptions> payments, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer)
        {
            return TypedResults.Forbid();
        }

        var unitId = new UnitId(request.UnitId);
        if (!await HoldsAsync(sales, buyer, unitId, cancellationToken)
            || await db.Baskets.Include(b => b.Lines).SingleOrDefaultAsync(b => b.BuyerId == buyer && b.UnitId == unitId && b.Status == BasketStatus.Open, cancellationToken) is not { Lines.Count: > 0 } basket)
        {
            return TypedResults.NotFound();
        }

        var progress = await db.UnitProgress.AsNoTracking().SingleAsync(p => p.UnitId == unitId, cancellationToken);
        var problems = BasketRules.Check(basket.Lines, await CatalogueAsync(db, basket.DevelopmentId, cancellationToken), progress.LatestMilestone, await BoughtAsync(db, unitId, basket.Id, cancellationToken))
            .Where(c => c.Problem is not null).Select(c => c.Problem!).ToList();
        if (problems.Count > 0)
        {
            return TypedResults.Problem(Problems.Business(http, "basket-invalid", "Basket needs changes", StatusCodes.Status422UnprocessableEntity, string.Join(" ", problems)));
        }

        basket.CheckoutAttempts++;
        basket.Status = BasketStatus.CheckingOut;
        await db.SaveChangesAsync(cancellationToken); // frozen first (C8 Lab 4: write intent, commit, call out)

        // C8's setting: Stripe sends the buyer back to My Home in the web app.
        var web = payments.Value.WebBaseUrl ?? throw new InvalidOperationException("Payments:WebBaseUrl is not set: the success and cancel pages live in the web app's My Home.");
        var session = await gateway.CreateBasketCheckoutSessionAsync(new BasketCheckoutRequest(
            basket.Id, tenant.TenantId!.Value, unitId, [.. basket.Lines.OrderBy(l => l.AddedAt).Select(l => new CheckoutLine(l.Name, new Money(l.UnitPrice, l.Currency), l.Quantity))],
            new Uri(web, "my-home?extras=paid"), new Uri(web, "my-home?extras=cancelled"), time.GetUtcNow().AddMinutes(30),
            $"extras-session:{basket.Id}:{basket.CheckoutAttempts}"), cancellationToken);
        basket.SessionId = session.Id;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new BasketCheckoutResponse(session.Id, session.Url, session.ExpiresAt));
    }

    internal static async Task<bool> HoldsAsync(IUnitAvailability sales, BuyerId buyer, UnitId unitId, CancellationToken cancellationToken) =>
        (await sales.ReservationsOfAsync(buyer, cancellationToken)).Any(r => r.UnitId == unitId && r.State is nameof(Held) or nameof(Confirmed));

    private static async Task<BasketRow> OpenBasketAsync(ExtrasDbContext db, BuyerId buyer, UnitId unitId, DevelopmentId developmentId, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await db.Baskets.Include(b => b.Lines).Where(b => b.BuyerId == buyer && b.UnitId == unitId && b.Status != BasketStatus.Paid)
                .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(cancellationToken) is { } existing)
        {
            return existing;
        }

        var basket = new BasketRow { BuyerId = buyer, UnitId = unitId, DevelopmentId = developmentId, CreatedAt = time.GetUtcNow() };
        db.Baskets.Add(basket);
        return basket;
    }

    internal static async Task<Dictionary<Guid, ExtraRow>> CatalogueAsync(ExtrasDbContext db, DevelopmentId developmentId, CancellationToken cancellationToken) =>
        await db.Extras.AsNoTracking().Where(e => e.DevelopmentId == developmentId).ToDictionaryAsync(e => e.Id, cancellationToken);

    /// <summary>What the home's buyer has already paid for in other baskets: it counts toward limits and conflicts.</summary>
    internal static async Task<List<BasketLineRow>> BoughtAsync(ExtrasDbContext db, UnitId unitId, Guid exceptBasket, CancellationToken cancellationToken) =>
        await db.Baskets.AsNoTracking().Where(b => b.UnitId == unitId && b.Id != exceptBasket && b.Status == BasketStatus.Paid)
            .SelectMany(b => b.Lines).Where(l => l.Status == BasketLineStatus.Paid).ToListAsync(cancellationToken);
}

public enum ExtrasPaymentOutcome
{
    /// <summary>Recorded: every line paid, or some refunded (they failed revalidation).</summary>
    Paid,

    /// <summary>Already recorded (a redelivered webhook): nothing changed.</summary>
    AlreadyPaid,

    /// <summary>No basket waiting for this session.</summary>
    NotFound,
}

/// <summary>
/// Extras' published write for Payments' webhook (C10 Lab 2): record a basket's payment inside the webhook's own
/// transaction (as Sales' <c>IReservationDeposits</c>, C8 Lab 2), and refund what failed revalidation after the commit.
/// </summary>
public interface IExtrasBasketPayments
{
    Task<ExtrasPaymentOutcome> ConfirmPaidAsync(Guid basketId, string sessionId, string paymentIntent, Money charged, DateTimeOffset paidAt, DbTransaction transaction, CancellationToken cancellationToken = default);

    Task ExpireCheckoutAsync(Guid basketId, string sessionId, DbTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>Refunds lines recorded as refund-pending; idempotent per line. Never inside a transaction.</summary>
    Task<int> RefundPendingAsync(Guid basketId, CancellationToken cancellationToken = default);
}

internal sealed class EfExtrasBasketPayments(ExtrasDbContext db, IServiceProvider services, TimeProvider time) : IExtrasBasketPayments
{
    public async Task<ExtrasPaymentOutcome> ConfirmPaidAsync(Guid basketId, string sessionId, string paymentIntent, Money charged, DateTimeOffset paidAt, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(charged);
        await EnlistAsync(transaction, cancellationToken);
        if (await db.Baskets.Include(b => b.Lines).SingleOrDefaultAsync(b => b.Id == basketId, cancellationToken) is not { } basket)
        {
            return ExtrasPaymentOutcome.NotFound;
        }

        if (basket.Status == BasketStatus.Paid)
        {
            return ExtrasPaymentOutcome.AlreadyPaid;
        }

        // Revalidate at the moment money moved: an extra may have closed (a milestone completed), or another basket for
        // the home been paid, since the Checkout page opened. Amounts are the lines' snapshots, never today's catalogue.
        var progress = await db.UnitProgress.AsNoTracking().SingleOrDefaultAsync(p => p.UnitId == basket.UnitId, cancellationToken);
        var catalogue = await BasketEndpoints.CatalogueAsync(db, basket.DevelopmentId, cancellationToken);
        var checks = BasketRules.Check(basket.Lines, catalogue, progress?.LatestMilestone,
            await BasketEndpoints.BoughtAsync(db, basket.UnitId, basket.Id, cancellationToken));
        foreach (var (line, problem) in checks)
        {
            line.Status = problem is null ? BasketLineStatus.Paid : BasketLineStatus.RefundPending;
            line.RefundReason = problem is null ? null : $"{problem} Its payment is refunded in full.";
        }

        basket.Status = BasketStatus.Paid;
        basket.SessionId = sessionId;
        basket.PaymentIntent = paymentIntent;
        basket.ChargedAmount = charged.Amount;
        basket.PaidAt = paidAt;
        var paid = basket.Lines.Where(l => l.Status == BasketLineStatus.Paid).OrderBy(l => l.AddedAt).ToList();
        if (paid.Count > 0)
        {
            db.Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.ExtrasPaid,
                new ExtrasPaidMessage(basket.Id, basket.UnitId.Value, db.CurrentTenantId, basket.BuyerId.Value,
                    [.. paid.Select(l => new PaidExtra(l.Id, l.ExtraId, l.Name, l.UnitPrice, l.Currency, l.Quantity, catalogue[l.ExtraId].ClosesAt.ToString()))], paid.Sum(l => l.Amount), paid[0].Currency, paidAt),
                db.CurrentTenantId, time.GetUtcNow()));
        }

        await db.SaveChangesAsync(cancellationToken);
        return ExtrasPaymentOutcome.Paid;
    }

    public async Task ExpireCheckoutAsync(Guid basketId, string sessionId, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        await EnlistAsync(transaction, cancellationToken);
        if (await db.Baskets.SingleOrDefaultAsync(b => b.Id == basketId && b.Status == BasketStatus.CheckingOut && b.SessionId == sessionId, cancellationToken) is { } basket)
        {
            basket.Status = BasketStatus.Open; // the page closed unpaid: the buyer can change the basket again
            basket.SessionId = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> RefundPendingAsync(Guid basketId, CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        // ConfirmPaidAsync joined the webhook's transaction, now committed: let go of it, or the retrying execution
        // strategy sees a user-initiated transaction and refuses every command.
        if (db.Database.CurrentTransaction is not null)
        {
            await db.Database.UseTransactionAsync(null, cancellationToken);
        }

        if (await db.Baskets.Include(b => b.Lines).SingleOrDefaultAsync(b => b.Id == basketId, cancellationToken) is not { PaymentIntent: { } intent } basket)
        {
            return 0;
        }

        var refunded = 0;
        foreach (var line in basket.Lines.Where(l => l.Status == BasketLineStatus.RefundPending && l.RefundReference is null))
        {
            // One key per line (C8 Lab 5): a retry or a redelivered webhook never refunds twice.
            var refund = await services.GetRequiredService<IPaymentGateway>().RefundAsync(intent, new Money(line.Amount, line.Currency), $"extras-refund:{line.Id}", cancellationToken);
            line.RefundReference = refund.Id;
            line.Status = BasketLineStatus.Refunded;
            await db.SaveChangesAsync(cancellationToken);
            refunded++;
        }

        return refunded;
    }

    private async Task EnlistAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        db.ChangeTracker.Clear();
        if (!ReferenceEquals(db.Database.GetDbConnection(), transaction.Connection))
        {
            db.Database.SetDbConnection(transaction.Connection);
        }

        await db.Database.UseTransactionAsync(transaction, cancellationToken);
    }
}
