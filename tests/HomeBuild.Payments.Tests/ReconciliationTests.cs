using HomeBuild.Domain;

namespace HomeBuild.Payments.Tests;

/// <summary>C8 Lab 6: the comparison rules, one case per mismatch kind.</summary>
public sealed class ReconciliationTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly Money Deposit = new(5000m, "GBP");

    [Fact]
    public void Matching_paid_session_and_deposit_report_nothing()
    {
        var r = ReservationId.New();
        Assert.Empty(Reconciliation.Compare([Paid("cs_1", r)], [], [Recorded("cs_1", r)]));
    }

    [Fact]
    public void Unpaid_sessions_are_not_money() =>
        Assert.Empty(Reconciliation.Compare([Paid("cs_open", ReservationId.New()) with { Paid = false }], [], []));

    [Fact]
    public void Paid_in_stripe_but_not_recorded() =>
        Assert.Equal(MismatchKind.PaidNotRecorded, Assert.Single(Reconciliation.Compare([Paid("cs_1", ReservationId.New())], [], [])).Kind);

    [Fact]
    public void Recorded_but_not_paid()
    {
        var r = ReservationId.New();
        Assert.Equal(MismatchKind.RecordedNotPaid, Assert.Single(Reconciliation.Compare([Paid("cs_1", r) with { Paid = false }], [], [Recorded("cs_1", r)])).Kind);
        Assert.Equal(MismatchKind.RecordedNotPaid, Assert.Single(Reconciliation.Compare([], [], [Recorded("cs_gone", r)])).Kind);
    }

    [Fact]
    public void Amount_differs()
    {
        var r = ReservationId.New();
        var m = Assert.Single(Reconciliation.Compare([Paid("cs_1", r) with { Amount = new Money(4000m, "GBP") }], [], [Recorded("cs_1", r)]));
        Assert.Equal((MismatchKind.AmountDiffers, 4000m, 5000m), (m.Kind, m.ProviderAmount!.Amount, m.HomeBuildAmount!.Amount));
    }

    [Fact]
    public void Refund_missing_in_homebuild()
    {
        var r = ReservationId.New();
        var m = Assert.Single(Reconciliation.Compare([Paid("cs_1", r)], [Refund("pi_cs_1")], [Recorded("cs_1", r)]));
        Assert.Equal(MismatchKind.RefundMissingInHomeBuild, m.Kind);
    }

    [Fact]
    public void Refund_missing_in_provider()
    {
        var r = ReservationId.New();
        var m = Assert.Single(Reconciliation.Compare([Paid("cs_1", r)], [], [Recorded("cs_1", r) with { RefundIntended = true, RefundReference = "re_1" }]));
        Assert.Equal(MismatchKind.RefundMissingInProvider, m.Kind);
    }

    [Fact]
    public void Refunded_on_both_sides_reports_nothing()
    {
        var r = ReservationId.New();
        Assert.Empty(Reconciliation.Compare([Paid("cs_1", r)], [Refund("pi_cs_1")], [Recorded("cs_1", r) with { RefundIntended = true, RefundReference = "re_1" }]));
    }

    private static ProviderSession Paid(string id, ReservationId r) => new(id, r, Tenant, true, Deposit, "pi_" + id, At);

    private static RecordedDeposit Recorded(string session, ReservationId r) => new(session, r, Tenant, Deposit, "pi_" + session, At, false, null);

    private static ProviderRefund Refund(string paymentIntent) => new("re_" + paymentIntent, paymentIntent, Deposit, "succeeded", At);
}
