using HomeBuild.Domain;

namespace HomeBuild.Payments;

/// <summary>A Checkout Session as the payment provider reports it (C8 Lab 6). No provider types: plain values.</summary>
public sealed record ProviderSession(string Id, ReservationId? ReservationId, Guid? TenantId, bool Paid, Money? Amount, string? PaymentReference, DateTimeOffset Created);

/// <summary>A refund as the provider reports it. <paramref name="PaymentReference"/> is the payment it gives back.</summary>
public sealed record ProviderRefund(string Id, string PaymentReference, Money Amount, string Status, DateTimeOffset Created);

/// <summary>A deposit as HomeBuild recorded it, with its refund (none, pending: intent stored, or done).</summary>
public sealed record RecordedDeposit(string SessionId, ReservationId ReservationId, Guid TenantId, Money Amount, string PaymentReference, DateTimeOffset PaidAt, bool RefundIntended, string? RefundReference);

public enum MismatchKind
{
    /// <summary>The provider took the money; HomeBuild has no deposit for the session.</summary>
    PaidNotRecorded,

    /// <summary>HomeBuild has a deposit; the provider has no paid session for it.</summary>
    RecordedNotPaid,

    AmountDiffers,

    /// <summary>The provider refunded a payment; HomeBuild does not show it refunded.</summary>
    RefundMissingInHomeBuild,

    /// <summary>HomeBuild refunded (or means to); the provider has no refund for that payment.</summary>
    RefundMissingInProvider,
}

public sealed record Mismatch(MismatchKind Kind, string SessionId, ReservationId? ReservationId, Money? ProviderAmount, Money? HomeBuildAmount, string Detail);

/// <summary>
/// C8 Lab 6: compares what the provider says happened with what HomeBuild recorded, by Checkout Session and by payment.
/// Unpaid sessions (open or expired) are not money, so they are not compared. The caller supplies the provider's
/// view of every session HomeBuild recorded in the window, even one created before it.
/// </summary>
public static class Reconciliation
{
    public static IReadOnlyList<Mismatch> Compare(IReadOnlyCollection<ProviderSession> sessions, IReadOnlyCollection<ProviderRefund> refunds, IReadOnlyCollection<RecordedDeposit> deposits)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(refunds);
        ArgumentNullException.ThrowIfNull(deposits);
        var mismatches = new List<Mismatch>();
        var depositBySession = deposits.ToDictionary(d => d.SessionId);
        var sessionById = sessions.ToDictionary(s => s.Id);
        var refunded = refunds.Where(r => r.Status is "succeeded" or "pending").GroupBy(r => r.PaymentReference).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var session in sessions.Where(s => s.Paid).OrderBy(s => s.Created))
        {
            if (!depositBySession.TryGetValue(session.Id, out var deposit))
            {
                mismatches.Add(new(MismatchKind.PaidNotRecorded, session.Id, session.ReservationId, session.Amount, null,
                    session.ReservationId is null ? "Paid, but not a HomeBuild session (no reservation in client_reference_id)." : "Paid, but HomeBuild has no deposit for this session."));
            }
            else if (session.Amount is { } amount && amount != deposit.Amount)
            {
                mismatches.Add(new(MismatchKind.AmountDiffers, session.Id, deposit.ReservationId, amount, deposit.Amount, $"Stripe {amount.Amount} {amount.Currency}, HomeBuild {deposit.Amount.Amount} {deposit.Amount.Currency}."));
            }
        }

        foreach (var deposit in deposits.OrderBy(d => d.PaidAt))
        {
            if (!sessionById.TryGetValue(deposit.SessionId, out var session) || !session.Paid)
            {
                mismatches.Add(new(MismatchKind.RecordedNotPaid, deposit.SessionId, deposit.ReservationId, session?.Amount, deposit.Amount,
                    session is null ? "Recorded, but the provider has no such session." : "Recorded, but the provider's session is not paid."));
            }

            var providerRefunds = refunded.GetValueOrDefault(deposit.PaymentReference) ?? [];
            if (deposit.RefundIntended && providerRefunds.Count == 0)
            {
                mismatches.Add(new(MismatchKind.RefundMissingInProvider, deposit.SessionId, deposit.ReservationId, null, deposit.Amount,
                    deposit.RefundReference is null ? "HomeBuild means to refund it (pending); the provider has no refund." : $"HomeBuild recorded refund {deposit.RefundReference}; the provider has none."));
            }
            else if (!deposit.RefundIntended && providerRefunds.Count > 0)
            {
                mismatches.Add(new(MismatchKind.RefundMissingInHomeBuild, deposit.SessionId, deposit.ReservationId, providerRefunds[0].Amount, deposit.Amount,
                    $"The provider refunded it ({string.Join(", ", providerRefunds.Select(r => r.Id))}); HomeBuild shows it kept."));
            }
        }

        var recordedPayments = deposits.Select(d => d.PaymentReference).ToHashSet();
        foreach (var refund in refunds.Where(r => r.Status is "succeeded" or "pending" && !recordedPayments.Contains(r.PaymentReference)))
        {
            var session = sessions.FirstOrDefault(s => s.PaymentReference == refund.PaymentReference);
            if (session?.ReservationId is not null)
            {
                mismatches.Add(new(MismatchKind.RefundMissingInHomeBuild, session.Id, session.ReservationId, refund.Amount, null,
                    $"The provider refunded {refund.PaymentReference} ({refund.Id}); HomeBuild has no deposit for it."));
            }
        }

        return mismatches;
    }
}
