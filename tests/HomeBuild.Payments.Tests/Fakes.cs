using HomeBuild.Domain;

namespace HomeBuild.Payments.Tests;

/// <summary>The fake payment gateway (C7 Lab 6): swapped in for Stripe without touching the core.</summary>
internal sealed class FakeGateway : IPaymentGateway
{
    public List<string> Keys { get; } = [];

    public string? Decline { get; set; }

    public Task<GatewayCharge> ChargeAsync(Money amount, string paymentMethodToken, string idempotencyKey, CancellationToken cancellationToken)
    {
        Keys.Add(idempotencyKey);
        return Task.FromResult(Decline is null ? new GatewayCharge(true, $"ch_{Keys.Count}", null) : new GatewayCharge(false, null, Decline));
    }

    public List<CheckoutRequest> Checkouts { get; } = [];

    public Task<CheckoutSession> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        Checkouts.Add(request);
        return Task.FromResult(new CheckoutSession($"cs_fake_{Checkouts.Count}", new Uri($"https://checkout.example/{Checkouts.Count}"), request.ExpiresAt));
    }
}

/// <summary>An in-memory <see cref="IPaymentStore"/>: the core's tests need no database.</summary>
internal sealed class FakePaymentStore : IPaymentStore
{
    public Dictionary<(UnitId, Milestone), StoredStagePayment> Stored { get; } = [];

    public Task<bool> ExistsAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.ContainsKey((unitId, milestone)));

    public Task<StoredStagePayment?> FindAsync(UnitId unitId, Milestone milestone, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.GetValueOrDefault((unitId, milestone)));

    public Task<bool> AddAsync(DueStagePayment payment, CancellationToken cancellationToken) =>
        Task.FromResult(Stored.TryAdd((payment.UnitId, payment.Milestone), new StoredStagePayment(payment, null, null)));

    public Task<IReadOnlyList<DueStagePayment>> UnpaidDueOnOrBeforeAsync(DateOnly dueOnOrBefore, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DueStagePayment>>([.. Stored.Values.Where(s => s.PaidAt is null && s.Due.DueFrom <= dueOnOrBefore).Select(s => s.Due).OrderBy(d => d.DueFrom)]);

    public Task MarkPaidAsync(UnitId unitId, Milestone milestone, string providerReference, DateTimeOffset paidAt, CancellationToken cancellationToken)
    {
        Stored[(unitId, milestone)] = Stored[(unitId, milestone)] with { PaidAt = paidAt, ProviderReference = providerReference };
        return Task.CompletedTask;
    }
}
