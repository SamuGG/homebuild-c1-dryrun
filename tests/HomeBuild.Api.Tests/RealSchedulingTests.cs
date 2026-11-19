using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using Amazon.SQS;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C9 Lab 6's end-to-end check, opt-in (HOMEBUILD_AWS_TESTS=1, AWS_PROFILE, HomeBuild-Scheduling deployed): real
/// EventBridge Scheduler and SQS, the test database. A reservation's expiry schedule is moved to two minutes ahead and
/// the API's own consumer expires the reservation when it fires; paying and cancelling delete their schedules.
/// The hold period cannot be under an hour (ReservationOptions), so the hold's end is moved in the database.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class RealSchedulingTests(PostgresFixture database)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";
    private static readonly UnitId Plot1 = new(Guid.Parse(EndpointTests.RiversidePlot1));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Log(Stopwatch clock, string text) => TestContext.Current.TestOutputHelper?.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} (+{clock.Elapsed.TotalSeconds:F0}s) {text}");

    [Fact]
    public async Task Expiry_schedule_fires_two_minutes_ahead_and_paying_or_cancelling_deletes_it()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1 and deploy HomeBuild-Scheduling.");
        using var sqs = new AmazonSQSClient(RegionEndpoint.EUWest2);
        using var scheduler = new AmazonSchedulerClient(RegionEndpoint.EUWest2);
        var queueUrl = (await sqs.GetQueueUrlAsync("homebuild-scheduled", Ct)).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"], Ct)).QueueARN;
        var account = queueArn.Split(':')[4];
        await using var api = await database.CreateApiAsync();
        await using var factory = api.WithWebHostBuilder(b => b
            .UseSetting("Payments:WebhookSecret", StripeTestEvents.Secret)
            .UseSetting("Scheduling:QueueArn", queueArn)
            .UseSetting("Scheduling:QueueUrl", queueUrl)
            .UseSetting("Scheduling:RoleArn", $"arn:aws:iam::{account}:role/homebuild-scheduler"));
        using var client = factory.CreateClient();
        var clock = Stopwatch.StartNew();

        // 1. Reserve: a one-time schedule at the hold's end (7 days).
        var a = await ReserveAsync(client);
        var created = await scheduler.GetScheduleAsync(new GetScheduleRequest { GroupName = "homebuild", Name = EventBridgeReservationSchedules.NameFor(a) }, Ct);
        Log(clock, $"reserved A; schedule {created.Name}: {created.ScheduleExpression} {created.ScheduleExpressionTimezone}, after completion {created.ActionAfterCompletion}, window {created.FlexibleTimeWindow.Mode}, state {created.State}");

        // 2. Move the hold's end to two minutes ahead (database), and the schedule with it (the adapter updates on conflict).
        var due = DateTimeOffset.UtcNow.AddMinutes(2);
        await using (var owner = new Npgsql.NpgsqlConnection(api.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await using var move = new Npgsql.NpgsqlCommand("UPDATE sales.reservations SET expires_at = @due, state = @state WHERE id = @id", owner);
            move.Parameters.AddWithValue("due", due);
            move.Parameters.AddWithValue("state", "Held|" + due.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            move.Parameters.AddWithValue("id", a.Value);
            Assert.Equal(1, await move.ExecuteNonQueryAsync(Ct));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IReservationSchedules>().ScheduleExpiryAsync(a, TestTenants.Demo, due, Ct);
        }

        var moved = await scheduler.GetScheduleAsync(new GetScheduleRequest { GroupName = "homebuild", Name = EventBridgeReservationSchedules.NameFor(a) }, Ct);
        Log(clock, $"hold of A moved to {due:HH:mm:ss.fff}; schedule now {moved.ScheduleExpression}");

        // 3. Wait for the schedule to fire and the API's consumer to expire A.
        while (await StateAsync(api, a) is not Expired)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(6), "A was not expired within 6 minutes");
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }

        var expired = (Expired)await StateAsync(api, a);
        Log(clock, $"A expired at {expired.At:HH:mm:ss.fff}, {(expired.At - due).TotalSeconds:F1} s after the hold's end");
        await using (var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo))
        {
            Log(clock, $"ReservationExpired rows in the outbox: {await sales.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.ReservationExpired, Ct)}; unit {(await sales.Units.AsNoTracking().SingleAsync(u => u.Id == Plot1, Ct)).Status}");
        }

        Log(clock, $"A's schedule after it ran: {await DescribeAsync(scheduler, a)}");

        // 4. Paying deletes the schedule.
        var b = await ReserveAsync(client);
        Log(clock, $"reserved B; schedule: {await DescribeAsync(scheduler, b)}");
        await PayAsync(client, b);
        Log(clock, $"paid B (state {(await StateAsync(api, b)).GetType().Name}); schedule: {await DescribeAsync(scheduler, b)}");
        Assert.Equal("deleted", await DescribeAsync(scheduler, b));

        // 5. Cancelling deletes the schedule (a staff cancel of the confirmed B; then a new hold, cancelled by the buyer).
        await CancelAsync(client, b, "sales-agent");
        var c = await ReserveAsync(client);
        Log(clock, $"reserved C; schedule: {await DescribeAsync(scheduler, c)}");
        await CancelAsync(client, c, "buyer");
        Log(clock, $"cancelled C; schedule: {await DescribeAsync(scheduler, c)}");
        Assert.Equal("deleted", await DescribeAsync(scheduler, c));
    }

    private static async Task<string> DescribeAsync(AmazonSchedulerClient scheduler, ReservationId id)
    {
        try
        {
            var s = await scheduler.GetScheduleAsync(new GetScheduleRequest { GroupName = "homebuild", Name = EventBridgeReservationSchedules.NameFor(id) }, Ct);
            return $"{s.ScheduleExpression} ({s.State})";
        }
        catch (ResourceNotFoundException)
        {
            return "deleted";
        }
    }

    private static async Task<ReservationId> ReserveAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{Plot1.Value}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer"));
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"reserve: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return new ReservationId(JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).GetProperty("id").GetGuid());
    }

    private static async Task PayAsync(HttpClient client, ReservationId id)
    {
        var payload = StripeTestEvents.Completed("evt_" + Guid.NewGuid().ToString("N"), "cs_" + Guid.NewGuid().ToString("N"), id);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", StripeTestEvents.Sign(payload, StripeTestEvents.Secret, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task CancelAsync(HttpClient client, ReservationId id, string role)
    {
        using var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/v1/reservations/{id.Value}");
        var who = role == "buyer" ? TestTokens.Users.Owner : TestTokens.Users.Staff(role);
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(who.ToString(), role));
        using var response = await client.SendAsync(cancel, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"cancel as {role}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<ReservationState> StateAsync(ApiFactory api, ReservationId id)
    {
        await using var sales = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return (await sales.Reservations.AsNoTracking().SingleAsync(r => r.Id == id, Ct)).State;
    }
}
