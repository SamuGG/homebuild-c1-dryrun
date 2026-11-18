using System.Net;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Payments.Reconcile;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Stripe;

namespace HomeBuild.Api.Tests;

/// <summary>C8 Lab 6's acceptance tests: the reconcile command over the database and a stub Stripe.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class ReconcileTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset From = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(1);

    [Fact]
    public async Task Matching_data_reports_zero_mismatches()
    {
        var (a, b) = (ReservationId.New(), ReservationId.New());
        var stripe = new ListStub([Session("cs_a", a, paid: true), Session("cs_b", b, paid: true), Session("cs_open", ReservationId.New(), paid: false)]);
        await using var api = await database.CreateApiAsync();
        await SeedAsync(api, ("cs_a", a), ("cs_b", b));

        var report = await RunAsync(api, stripe);

        Assert.Equal((3, 2, 2), (report.StripeSessions, report.StripePaidSessions, report.Deposits));
        Assert.Empty(report.Mismatches);
    }

    // The lab's seeded mismatch: a deposit row deleted (a lost webhook, a bug, a manual change).
    [Fact]
    public async Task Seeded_mismatch_is_reported()
    {
        var (kept, lost) = (ReservationId.New(), ReservationId.New());
        var stripe = new ListStub([Session("cs_kept", kept, paid: true), Session("cs_lost", lost, paid: true)]);
        await using var api = await database.CreateApiAsync();
        await SeedAsync(api, ("cs_kept", kept), ("cs_lost", lost));
        await using (var payments = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo))
        {
            payments.Deposits.Remove(payments.Deposits.Single(d => d.SessionId == "cs_lost"));
            await payments.SaveChangesAsync(Ct);
        }

        var report = await RunAsync(api, stripe);

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal((MismatchKind.PaidNotRecorded, "cs_lost", lost), (mismatch.Kind, mismatch.SessionId, mismatch.ReservationId));
        Assert.Contains("PaidNotRecorded,cs_lost,", ReconcileCommand.ToCsv(report), StringComparison.Ordinal);
    }

    private static async Task<ReconciliationReport> RunAsync(ApiFactory api, ListStub stripe)
    {
        await using var factory = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStripeClient>(
            new StripeClient("stub-key-not-a-secret", httpClient: new SystemNetHttpClient(new HttpClient(stripe), maxNetworkRetries: 0)))));
        await using var scope = factory.Services.CreateAsyncScope();
        return await ActivatorUtilities.CreateInstance<ReconcileCommand>(scope.ServiceProvider).RunAsync(From, To, Ct);
    }

    private static async Task SeedAsync(ApiFactory api, params (string Session, ReservationId Reservation)[] deposits)
    {
        await using var payments = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        payments.Deposits.AddRange(deposits.Select(d => new DepositRow
        {
            ReservationId = d.Reservation, SessionId = d.Session, ProviderReference = "pi_" + d.Session, Amount = 5000m, Currency = "GBP", PaidAt = From.AddHours(12),
        }));
        await payments.SaveChangesAsync(Ct);
    }

    private static Dictionary<string, object?> Session(string id, ReservationId reservation, bool paid) => new()
    {
        ["id"] = id, ["object"] = "checkout.session", ["client_reference_id"] = reservation.Value.ToString(),
        ["metadata"] = new Dictionary<string, string> { ["tenant_id"] = TestTenants.Demo.ToString() },
        ["payment_status"] = paid ? "paid" : "unpaid", ["status"] = paid ? "complete" : "open", ["amount_total"] = 500000, ["currency"] = "gbp",
        ["payment_intent"] = paid ? "pi_" + id : null, ["created"] = From.AddHours(11).ToUnixTimeSeconds(), ["livemode"] = false,
    };

    /// <summary>Answers Stripe's list calls (sessions, refunds) and single-session reads, like Stripe's list objects.</summary>
    private sealed class ListStub(IReadOnlyList<Dictionary<string, object?>> sessions) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = path switch
            {
                "/v1/checkout/sessions" => List(path, sessions),
                "/v1/refunds" => List(path, []),
                _ when path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal) =>
                    sessions.FirstOrDefault(s => (string)s["id"]! == path[22..]) ?? (object)new { error = new { type = "invalid_request_error", code = "resource_missing", message = "No such checkout.session" } },
                _ => throw new InvalidOperationException($"Unexpected Stripe call {request.Method} {path}"),
            };
            var status = body is Dictionary<string, object?> or ListBody ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }

        private static ListBody List(string url, IReadOnlyList<Dictionary<string, object?>> data) => new("list", data, false, url);
    }

    private sealed record ListBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("object")] string Object,
        [property: System.Text.Json.Serialization.JsonPropertyName("data")] IReadOnlyList<Dictionary<string, object?>> Data,
        [property: System.Text.Json.Serialization.JsonPropertyName("has_more")] bool HasMore,
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url);
}
