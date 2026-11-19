using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon;
using Amazon.SQS;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 1 end to end, opt-in (HOMEBUILD_AWS_TESTS=1, HOMEBUILD_TOPIC_ARN, HOMEBUILD_NOTIFICATIONS_BUCKET, AWS_PROFILE;
/// HomeBuild-Messaging with homebuild-extras and HomeBuild-Notifications deployed): a site manager completes Frame;
/// Construction's relay publishes MilestoneCompleted; homebuild-extras delivers it to this API's Extras consumer, which
/// records the unit's progress and writes ExtrasUnlocked to Extras' outbox; Extras' relay publishes it; the
/// notifications Lambda writes it to S3; My Home shows the open extras and the notification.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class RealExtrasTests(PostgresFixture database)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Completing_frame_unlocks_extras_and_tells_the_buyer()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1, HOMEBUILD_TOPIC_ARN and HOMEBUILD_NOTIFICATIONS_BUCKET.");
        var topic = Environment.GetEnvironmentVariable("HOMEBUILD_TOPIC_ARN") ?? throw new InvalidOperationException("Set HOMEBUILD_TOPIC_ARN.");
        var bucket = Environment.GetEnvironmentVariable("HOMEBUILD_NOTIFICATIONS_BUCKET") ?? throw new InvalidOperationException("Set HOMEBUILD_NOTIFICATIONS_BUCKET.");
        using var sqs = new AmazonSQSClient(RegionEndpoint.EUWest2);
        var extrasQueue = (await sqs.GetQueueUrlAsync("homebuild-extras", Ct)).QueueUrl;
        await using var api = await database.CreateApiAsync();
        await using var factory = api.WithWebHostBuilder(b => b
            .UseSetting("Messaging:TopicArn", topic)
            .UseSetting("Messaging:RelayEnabled", "true")
            .UseSetting("Messaging:RelayInterval", "00:00:01")
            .UseSetting("Extras:QueueUrl", extrasQueue)
            .UseSetting("Extras:Consumer:Wait", "00:00:05")
            .UseSetting("Notifications:BucketName", bucket));
        using var client = factory.CreateClient();
        var buyer = TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer");
        var clock = Stopwatch.StartNew();
        void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s {text}");

        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{EndpointTests.RiversidePlot1}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) })
        {
            reserve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(reserve, Ct)).StatusCode);
        }

        Log($"my extras before: {await GetAsync(client, "/v2/me/extras", buyer)}");
        using (var complete = new HttpRequestMessage(HttpMethod.Post, $"/v2/units/{EndpointTests.RiversidePlot1}/milestones/Frame/completion")
        {
            Content = JsonContent.Create(new { completedOn = DateOnly.FromDateTime(DateTime.UtcNow) }),
        })
        {
            complete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));
            using var completed = await client.SendAsync(complete, Ct);
            Assert.True(completed.IsSuccessStatusCode, $"complete Frame: {completed.StatusCode} {await completed.Content.ReadAsStringAsync(Ct)}");
        }

        Log("Frame completed for Riverside plot 1");
        string extras;
        while (!(extras = await GetAsync(client, "/v2/me/extras", buyer)).Contains("Premium kitchen", StringComparison.Ordinal))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), "extras did not open within 2 minutes");
            await Task.Delay(500, Ct);
        }

        Log($"GET /v2/me/extras: {extras}");
        string notifications;
        while (!(notifications = await GetAsync(client, "/v2/me/notifications", buyer)).Contains("New extras are open", StringComparison.Ordinal))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(3), "no extras notification within 3 minutes");
            await Task.Delay(1000, Ct);
        }

        Log($"GET /v2/me/notifications: {notifications}");
    }

    private static async Task<string> GetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Serialize(JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)));
    }
}
