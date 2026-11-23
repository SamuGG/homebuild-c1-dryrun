using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HomeBuild.Api.Modules.Aftercare;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Tests;

/// <summary>C10 Lab 4 through the API: handover, the snag lifecycle with both parties, and the two DTOs.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class AftercareTests(PostgresFixture database)
{
    private static readonly Guid Unit = Guid.Parse(EndpointTests.RiversidePlot1);
    internal static readonly string Buyer = TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer");
    internal static readonly string OtherBuyer = TestTokens.Access(TestTokens.Users.OtherBuyer.ToString(), "buyer");
    internal static readonly string Aftercare = TestTokens.Access(TestTokens.Users.Staff("aftercare").ToString(), "aftercare");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Handover completes when the checklist is done: the defects period starts (two years by default), HandoverCompleted
    // goes through the outbox, and the buyer's My Home gets the pack.
    [Fact]
    public async Task Completed_handover_starts_the_defects_period_and_publishes_the_pack()
    {
        var (api, factory, client) = await StartAsync(database);
        await using var _ = api;
        await using var __ = factory;

        var partial = await SendAsync(client, HttpMethod.Put, $"/v2/units/{Unit}/handover", Aftercare, new { keysHandedOver = true, meterReadings = "", warrantiesIssued = false });
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("defectsEndOn").ValueKind); // not complete yet
        var done = await SendAsync(client, HttpMethod.Put, $"/v2/units/{Unit}/handover", Aftercare, new { keysHandedOver = true, meterReadings = "Gas 00012, electricity 00034", warrantiesIssued = true });

        var completedOn = DateOnly.FromDateTime(done.GetProperty("completedAt").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(completedOn.AddYears(2).AddDays(-1), DateOnly.Parse(done.GetProperty("defectsEndOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        var pack = Assert.Single((await SendAsync(client, HttpMethod.Get, "/v2/me/handover", Buyer)).EnumerateArray());
        Assert.Contains("Home warranty certificate", pack.GetProperty("documents").EnumerateArray().Select(d => d.GetString()));
        await using var db = AftercareModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.HandoverCompleted, Ct));
        using var again = await RawAsync(client, HttpMethod.Put, $"/v2/units/{Unit}/handover", Aftercare, new { keysHandedOver = true, meterReadings = "x", warrantiesIssued = true });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Internal_notes_not_in_buyer_dto()
    {
        var (api, factory, client) = await StartAsync(database);
        await using var _ = api;
        await using var __ = factory;
        var id = await DraftAndReportAsync(client);

        using (var note = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/notes", Aftercare, new { text = "Subcontractor caused this; recharge them" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, note.StatusCode);
        }

        using (var comment = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/comments", Aftercare, new { text = "We will be in touch this week" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, comment.StatusCode);
        }

        var buyerJson = JsonSerializer.Serialize(await SendAsync(client, HttpMethod.Get, "/v2/me/snags", Buyer));
        Assert.DoesNotContain("recharge", buyerJson, StringComparison.Ordinal);
        Assert.Contains("We will be in touch this week", buyerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("actorId", buyerJson, StringComparison.Ordinal);
        var staff = (await SendAsync(client, HttpMethod.Get, $"/v2/snags?unitId={Unit}", Aftercare))[0];
        Assert.Contains(staff.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("internal").GetBoolean() && e.GetProperty("text").GetString()!.Contains("recharge", StringComparison.Ordinal));

        using var buyerNote = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/notes", Buyer, new { text = "sneaky" });
        Assert.Equal(HttpStatusCode.Forbidden, buyerNote.StatusCode);
    }

    // The whole two-party lifecycle through the API, with the refusals: the buyer cannot schedule, Aftercare cannot
    // confirm, and another buyer cannot touch the snag (404, as if it did not exist).
    [Fact]
    public async Task Both_parties_move_a_snag_through_its_lifecycle()
    {
        var (api, factory, client) = await StartAsync(database);
        await using var _ = api;
        await using var __ = factory;
        var id = await DraftAndReportAsync(client);

        Assert.Equal("Acknowledged", (await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/acknowledge", Aftercare)).GetProperty("state").GetString());
        using (var buyerSchedules = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/schedule", Buyer, new { subcontractor = "Me", visitOn = "2026-12-01" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, buyerSchedules.StatusCode);
            Assert.Contains("Only Aftercare may schedule a snag that is acknowledged.", await buyerSchedules.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var stranger = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/comments", OtherBuyer, new { text = "hello" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        }

        await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/schedule", Aftercare, new { subcontractor = "Northgate Plastering", visitOn = "2026-11-25" });
        await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/fix", Aftercare);
        using (var aftercareConfirms = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/confirm", Aftercare))
        {
            Assert.Equal(HttpStatusCode.Conflict, aftercareConfirms.StatusCode);
        }

        await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/dispute", Buyer, new { reason = "The crack is still there" });
        var rescheduled = await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/schedule", Aftercare, new { subcontractor = "Northgate Plastering", visitOn = "2026-12-01" });
        Assert.Equal(("Scheduled", "2026-12-01"), (rescheduled.GetProperty("state").GetString(), rescheduled.GetProperty("visitOn").GetString()));
        await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/fix", Aftercare);
        await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/confirm", Buyer);
        var closed = await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/close", Aftercare);

        Assert.Equal("Closed", closed.GetProperty("state").GetString());
        Assert.Equal(["Reported", "Acknowledged", "Scheduled", "Fixed", "Disputed", "Scheduled", "Fixed", "Confirmed", "Closed"],
            closed.GetProperty("timeline").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "Transition").Select(e => e.GetProperty("to").GetString()));
    }

    /// <summary>The owner's reservation of Riverside plot 1, confirmed by a signed deposit webhook.</summary>
    internal static async Task<(ApiFactory Api, WebApplicationFactory<Program> Factory, HttpClient Client)> StartAsync(PostgresFixture database, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null)
    {
        var api = await database.CreateApiAsync();
        var factory = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("Payments:WebhookSecret", StripeTestEvents.Secret);
            configure?.Invoke(b);
        });
        var client = factory.CreateClient();
        var reservation = await SendAsync(client, HttpMethod.Post, $"/v1/units/{Unit}/reservations", Buyer, new { deposit = 5000m, currency = "GBP" });
        var payload = StripeTestEvents.Completed("evt_" + Guid.NewGuid().ToString("N"), "cs_" + Guid.NewGuid().ToString("N"), new ReservationId(reservation.GetProperty("id").GetGuid()));
        using var webhook = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        webhook.Headers.Add("Stripe-Signature", StripeTestEvents.Sign(payload, StripeTestEvents.Secret, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(webhook, Ct)).StatusCode);
        return (api, factory, client);
    }

    internal static async Task<Guid> DraftAndReportAsync(HttpClient client)
    {
        var draft = await SendAsync(client, HttpMethod.Post, "/v2/me/snags", Buyer, new { unitId = Unit, area = "Kitchen", description = "Crack in the ceiling", severity = "Minor" });
        Assert.Equal("Draft", draft.GetProperty("state").GetString());
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal("Reported", (await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/report", Buyer)).GetProperty("state").GetString());
        return id;
    }

    internal static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        using var response = await RawAsync(client, method, path, token, body);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    internal static async Task<HttpResponseMessage> RawAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post && path.EndsWith("/reservations", StringComparison.Ordinal))
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        return await client.SendAsync(request, Ct);
    }
}
