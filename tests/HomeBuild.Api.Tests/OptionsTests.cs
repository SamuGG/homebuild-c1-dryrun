using HomeBuild.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Tests;

public class OptionsTests
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Reservation:HoldPeriod"] = "7.00:00:00",
        ["Reservation:MinimumDeposit"] = "1000",
        ["Reservation:Currency"] = "GBP",
    };

    internal static IHost BuildHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddReservationPolicy();
        return builder.Build();
    }

    private static Dictionary<string, string?> With(string key, string? value)
    {
        var copy = new Dictionary<string, string?>(Valid);
        if (value is null)
        {
            copy.Remove(key);
        }
        else
        {
            copy[key] = value;
        }

        return copy;
    }

    [Fact]
    public async Task Host_fails_to_start_when_hold_period_is_missing()
    {
        using var host = BuildHost(With("Reservation:HoldPeriod", null));
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Host_fails_to_start_when_minimum_deposit_is_zero()
    {
        using var host = BuildHost(With("Reservation:MinimumDeposit", "0"));
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validation_error_names_every_invalid_setting()
    {
        using var host = BuildHost(new()
        {
            ["Reservation:HoldPeriod"] = "00:10:00",
            ["Reservation:MinimumDeposit"] = "0",
            ["Reservation:Currency"] = "gbp",
        });
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("HoldPeriod", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MinimumDeposit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Currency", ex.Message, StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine(ex.Message);
    }

    [Fact]
    public void Valid_configuration_produces_ReservationPolicy()
    {
        using var host = BuildHost(Valid);
        var policy = host.Services.GetRequiredService<ReservationPolicy>();
        Assert.Equal(new ReservationPolicy(TimeSpan.FromDays(7), new Money(1000m, "GBP")), policy);
    }
}
