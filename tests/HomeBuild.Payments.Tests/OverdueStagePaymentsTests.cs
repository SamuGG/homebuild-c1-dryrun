using HomeBuild.Domain;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Payments.Tests;

/// <summary>C7 Lab 6, the comparison feature in Payments' style: the rule tested with an in-memory store.</summary>
public class OverdueStagePaymentsTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    [Theory]
    [InlineData(14, false)] // the 14th day after it fell due: still within terms
    [InlineData(15, true)]
    [InlineData(40, true)]
    [InlineData(0, false)]
    public async Task Stage_payment_is_overdue_after_14_days_unpaid(int daysSinceDue, bool overdue)
    {
        var store = new FakePaymentStore();
        await store.AddAsync(new DueStagePayment(UnitId.New(), Milestone.Roof, Amount(15_000m), Today.AddDays(-daysSinceDue)), CancellationToken.None);

        var result = await new ListOverdueStagePayments(store).HandleAsync(Today, CancellationToken.None);

        Assert.Equal(overdue, result.Count == 1);
        if (overdue)
        {
            Assert.Equal(daysSinceDue - 14, result[0].DaysOverdue);
        }
    }

    [Fact]
    public async Task Paid_payments_are_never_overdue_and_the_most_overdue_comes_first()
    {
        var store = new FakePaymentStore();
        var (late, later, paid) = (UnitId.New(), UnitId.New(), UnitId.New());
        await store.AddAsync(new DueStagePayment(late, Milestone.Roof, Amount(15_000m), Today.AddDays(-20)), CancellationToken.None);
        await store.AddAsync(new DueStagePayment(later, Milestone.Foundations, Amount(10_000m), Today.AddDays(-60)), CancellationToken.None);
        await store.AddAsync(new DueStagePayment(paid, Milestone.Roof, Amount(15_000m), Today.AddDays(-60)), CancellationToken.None);
        await store.MarkPaidAsync(paid, Milestone.Roof, "ch_1", DateTimeOffset.UnixEpoch, CancellationToken.None);

        var result = await new ListOverdueStagePayments(store).HandleAsync(Today, CancellationToken.None);

        Assert.Equal([later, late], result.Select(p => p.UnitId));
    }
}
