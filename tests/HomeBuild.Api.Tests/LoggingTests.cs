using HomeBuild.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace HomeBuild.Api.Tests;

public class LoggingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReservationPolicy Policy = new(TimeSpan.FromDays(7), new Money(1000m, "GBP"));

    private static Unit NewUnit() => new(UnitId.New(), DevelopmentId.New(), "P-01", new Money(250_000m, "GBP"));

    [Fact]
    public void Unit_reserved_is_logged_with_UnitId_and_ReservationId_properties()
    {
        var logger = new FakeLogger();
        var unit = NewUnit();
        var result = unit.Reserve(BuyerId.New(), new Money(5000m, "GBP"), Policy, Now);
        logger.LogOutcome(unit.Id, result);

        var record = logger.LatestRecord;
        Assert.Equal(1001, record.Id.Id);
        var reservation = Assert.IsType<Reserved>(result.Value).Reservation;
        Assert.Contains(record.StructuredState!, kv => kv.Key == "UnitId" && kv.Value == unit.Id.ToString());
        Assert.Contains(record.StructuredState!, kv => kv.Key == "ReservationId" && kv.Value == reservation.Id.ToString());
    }

    [Fact]
    public void Rejected_reservation_is_logged_at_Information_with_reason()
    {
        var logger = new FakeLogger();
        var unit = NewUnit();
        logger.LogOutcome(unit.Id, unit.Reserve(BuyerId.New(), new Money(1m, "GBP"), Policy, Now));

        var record = logger.LatestRecord;
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal(1002, record.Id.Id);
        Assert.Contains(record.StructuredState!, kv => kv.Key == "Reason" && !string.IsNullOrEmpty(kv.Value));
    }

    [Fact]
    public async Task Startup_logs_the_reservation_policy_once()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reservation:HoldPeriod"] = "7.00:00:00",
            ["Reservation:MinimumDeposit"] = "1000",
            ["Reservation:Currency"] = "GBP",
        });
        builder.Logging.AddFakeLogging();
        builder.Services.AddReservationPolicy();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var records = host.Services.GetFakeLogCollector().GetSnapshot();
        Assert.Single(records, r => r.Id.Id == 1004 && r.Level == LogLevel.Information);
    }
}
