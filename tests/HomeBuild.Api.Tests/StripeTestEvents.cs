using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeBuild.Domain;

namespace HomeBuild.Api.Tests;

/// <summary>Stripe events built and signed the way Stripe does it, for the webhook and race tests (C8 Labs 2 and 5).</summary>
internal static class StripeTestEvents
{
    /// <summary>Generated at run time; Stripe's secrets have this prefix, and the value is any string.</summary>
    public static readonly string Secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    /// <summary>Stripe's scheme: t=timestamp, v1=hex(HMAC-SHA256(secret, "{t}.{payload}")).</summary>
    public static string Sign(string payload, string secret, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    public static string Completed(string eventId, string sessionId, ReservationId reservation) =>
        Event(eventId, "checkout.session.completed", sessionId, reservation, "complete", "paid");

    public static string Expired(string eventId, string sessionId, ReservationId reservation) =>
        Event(eventId, "checkout.session.expired", sessionId, reservation, "expired", "unpaid");

    /// <summary>An event shaped like Stripe's, indented as Stripe sends it.</summary>
    public static string Event(string eventId, string type, string sessionId, ReservationId reservation, string status, string paymentStatus) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["api_version"] = Stripe.StripeConfiguration.ApiVersion,
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["livemode"] = false,
            ["type"] = type,
            ["data"] = new Dictionary<string, object?>
            {
                ["object"] = new Dictionary<string, object?>
                {
                    ["id"] = sessionId,
                    ["object"] = "checkout.session",
                    ["client_reference_id"] = reservation.Value.ToString(),
                    ["metadata"] = new Dictionary<string, string>
                    {
                        ["tenant_id"] = TestTenants.Demo.ToString(),
                        ["unit_id"] = EndpointTests.RiversidePlot1,
                        ["reservation_id"] = reservation.Value.ToString(),
                    },
                    ["amount_total"] = 500000,
                    ["currency"] = "gbp",
                    ["payment_intent"] = status == "complete" ? "pi_" + sessionId : null,
                    ["status"] = status,
                    ["payment_status"] = paymentStatus,
                    ["livemode"] = false,
                },
            },
        }, Indented);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
