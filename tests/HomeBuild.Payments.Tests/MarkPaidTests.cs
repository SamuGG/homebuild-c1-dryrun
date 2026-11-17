using HomeBuild.Domain;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Payments.Tests;

/// <summary>C7 Lab 6: the MarkPaid use case, with the fake gateway and an in-memory store (no database, no Stripe).</summary>
public class MarkPaidTests
{
    private static readonly UnitId Unit = UnitId.New();
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Due_stage_payment_is_charged_once_and_marked_paid()
    {
        var (markPaid, gateway, store) = Create();
        await store.AddAsync(new DueStagePayment(Unit, Milestone.Roof, Amount(15_000m), new DateOnly(2026, 11, 17)), Ct);

        var first = await markPaid.HandleAsync(new PayStagePayment(Unit, Milestone.Roof, "pm_card"), Ct);
        var second = await markPaid.HandleAsync(new PayStagePayment(Unit, Milestone.Roof, "pm_card"), Ct);

        var paid = Assert.IsType<StagePaymentPaid>(first.Value);
        Assert.Equal((Amount(15_000m), "ch_1"), (paid.Amount, paid.ProviderReference));
        Assert.Equal(paid, second.Value);
        Assert.Equal([$"stage-{Unit.Value}-Roof"], gateway.Keys);
    }

    [Fact]
    public async Task Payment_that_is_not_due_is_not_charged()
    {
        var (markPaid, gateway, _) = Create();

        var result = await markPaid.HandleAsync(new PayStagePayment(Unit, Milestone.Frame, "pm_card"), Ct);

        Assert.IsType<StagePaymentNotDue>(result.Value);
        Assert.Empty(gateway.Keys);
    }

    [Fact]
    public async Task Declined_charge_leaves_the_payment_unpaid()
    {
        var (markPaid, gateway, store) = Create();
        gateway.Decline = "card_declined";
        await store.AddAsync(new DueStagePayment(Unit, Milestone.Roof, Amount(15_000m), new DateOnly(2026, 11, 17)), Ct);

        var result = await markPaid.HandleAsync(new PayStagePayment(Unit, Milestone.Roof, "pm_card"), Ct);

        Assert.Equal("card_declined", Assert.IsType<StagePaymentDeclined>(result.Value).Reason);
        Assert.Null((await store.FindAsync(Unit, Milestone.Roof, Ct))!.PaidAt);
    }

    private static (MarkPaid MarkPaid, FakeGateway Gateway, FakePaymentStore Store) Create()
    {
        var gateway = new FakeGateway();
        var store = new FakePaymentStore();
        return (new MarkPaid(gateway, store, TimeProvider.System), gateway, store);
    }
}
