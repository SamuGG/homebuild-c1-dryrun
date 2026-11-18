using HomeBuild.Api.Modules.Payments;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.Extensions.Configuration;
using Stripe;

namespace HomeBuild.Api.Tests;

/// <summary>
/// Opt-in (HOMEBUILD_STRIPE_TESTS=1), like C5's real-S3 tests: these call Stripe in test mode with the developer's
/// user-secrets key. CI never sets the variable and has no key. They create unpaid test-mode Checkout Sessions, which
/// Stripe expires on its own.
/// </summary>
public sealed class RealStripeTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_STRIPE_TESTS") == "1";

    [Fact]
    public async Task Retried_session_create_with_the_same_key_returns_the_same_session()
    {
        Assert.SkipUnless(Enabled, "Stripe test mode: set HOMEBUILD_STRIPE_TESTS=1.");
        var gateway = new StripePaymentGateway(Client());
        var request = Request(ReservationId.New(), DateTimeOffset.UtcNow.AddHours(2));

        var first = await gateway.CreateCheckoutSessionAsync(request, TestContext.Current.CancellationToken);
        var retried = await gateway.CreateCheckoutSessionAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, retried.Id);
        TestContext.Current.TestOutputHelper!.WriteLine($"same key, same parameters: {first.Id} then {retried.Id}");
    }

    [Fact]
    public async Task Same_key_with_a_later_expiry_is_refused_by_Stripe()
    {
        Assert.SkipUnless(Enabled, "Stripe test mode: set HOMEBUILD_STRIPE_TESTS=1.");
        var gateway = new StripePaymentGateway(Client());
        var request = Request(ReservationId.New(), DateTimeOffset.UtcNow.AddHours(23).AddMinutes(58));
        var first = await gateway.CreateCheckoutSessionAsync(request, TestContext.Current.CancellationToken);

        // The 24-hour cap moves with the clock: a retry a minute later asks for a later expires_at under the same key.
        var e = await Assert.ThrowsAsync<StripeException>(() =>
            gateway.CreateCheckoutSessionAsync(request with { ExpiresAt = request.ExpiresAt.AddMinutes(1) }, TestContext.Current.CancellationToken));

        TestContext.Current.TestOutputHelper!.WriteLine($"first {first.Id}; retry with a later expires_at: HTTP {(int)e.HttpStatusCode} {e.StripeError?.Type}: {e.StripeError?.Message}");
        Assert.Equal("idempotency_error", e.StripeError?.Type);
    }

    private static CheckoutRequest Request(ReservationId reservation, DateTimeOffset expiresAt) =>
        new(reservation, Guid.Parse("0198a000-0000-7000-8000-00000000000a"), UnitId.New(), new Money(5000m, "GBP"), "HomeBuild opt-in test",
            new Uri("http://localhost:5266/my-home?deposit=paid"), new Uri("http://localhost:5266/my-home?deposit=cancelled"), expiresAt,
            $"deposit-session:{reservation.Value}"); // the key StartDepositCheckout derives

    private static StripeClient Client()
    {
        var key = new ConfigurationBuilder().AddUserSecrets<Program>().Build()["Payments:SecretKey"];
        Assert.SkipWhen(string.IsNullOrEmpty(key), "No Payments:SecretKey in the API's user-secrets.");
        Assert.SkipUnless(key!.StartsWith("sk_test_", StringComparison.Ordinal) || key.StartsWith("rk_test_", StringComparison.Ordinal), "Only a test-mode key may be used.");
        return new StripeClient(key);
    }
}
