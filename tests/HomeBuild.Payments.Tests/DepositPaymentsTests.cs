using HomeBuild.Domain;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Payments.Tests;

public sealed class DepositPaymentsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deposit_is_charged_once_and_recorded()
    {
        var (payments, gateway, records) = Create();
        var command = new TakeDepositPayment(ReservationId.New(), Amount(5_000m), "pm_card_visa");

        var paid = Assert.IsType<DepositPaid>(await payments.TakeAsync(command, Ct));

        Assert.Equal(Amount(5_000m), paid.Amount);
        Assert.Equal($"deposit-{command.ReservationId}", Assert.Single(gateway.Keys));
        Assert.Same(paid, records.Stored[command.ReservationId]);
    }

    [Fact]
    public async Task Taking_the_same_deposit_again_charges_nothing_and_returns_the_payment()
    {
        var (payments, gateway, _) = Create();
        var command = new TakeDepositPayment(ReservationId.New(), Amount(5_000m), "pm_card_visa");

        var first = await payments.TakeAsync(command, Ct);
        var second = await payments.TakeAsync(command, Ct);

        Assert.Equal(first, second);
        Assert.Single(gateway.Keys);
    }

    [Fact]
    public async Task Declined_charge_is_a_failure_and_records_nothing()
    {
        var (payments, gateway, records) = Create();
        gateway.Decline = "card_declined";

        var failed = Assert.IsType<DepositPaymentFailed>(await payments.TakeAsync(new(ReservationId.New(), Amount(5_000m), "pm_card_chargeDeclined"), Ct));

        Assert.Equal("card_declined", failed.Reason);
        Assert.Empty(records.Stored);
    }

    public static TheoryData<decimal, string, string?> Commands => new()
    {
        new TheoryDataRow<decimal, string, string?>(5_000m, "pm_card_visa", null).WithTestDisplayName("Payment rule: a positive deposit with a payment method is valid"),
        new TheoryDataRow<decimal, string, string?>(0m, "pm_card_visa", "A deposit must be more than zero.").WithTestDisplayName("Payment rule: a zero deposit is refused"),
        new TheoryDataRow<decimal, string, string?>(5_000m, " ", "A payment method is required.").WithTestDisplayName("Payment rule: a blank payment method is refused"),
    };

    // DepositPaymentRules is internal: visible here through the core's InternalsVisibleTo, and nowhere else.
    [Theory]
    [MemberData(nameof(Commands))]
    public void Deposit_payment_rules(decimal amount, string token, string? problem) =>
        Assert.Equal(problem, DepositPaymentRules.Problem(new TakeDepositPayment(ReservationId.New(), Amount(amount), token)));

    private static (DepositPayments Payments, FakeGateway Gateway, FakeRecords Records) Create()
    {
        var gateway = new FakeGateway();
        var records = new FakeRecords();
        return (new DepositPayments(gateway, records, TimeProvider.System), gateway, records);
    }

    private sealed class FakeGateway : IPaymentGateway
    {
        public List<string> Keys { get; } = [];

        public string? Decline { get; set; }

        public Task<GatewayCharge> ChargeAsync(Money amount, string paymentMethodToken, string idempotencyKey, CancellationToken cancellationToken)
        {
            Keys.Add(idempotencyKey);
            return Task.FromResult(Decline is null ? new GatewayCharge(true, $"ch_{Keys.Count}", null) : new GatewayCharge(false, null, Decline));
        }
    }

    private sealed class FakeRecords : IPaymentRecords
    {
        public Dictionary<ReservationId, DepositPaid> Stored { get; } = [];

        public Task<DepositPaid?> FindAsync(ReservationId reservationId, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.GetValueOrDefault(reservationId));

        public Task AddAsync(DepositPaid paid, CancellationToken cancellationToken)
        {
            Stored[paid.ReservationId] = paid;
            return Task.CompletedTask;
        }
    }
}
