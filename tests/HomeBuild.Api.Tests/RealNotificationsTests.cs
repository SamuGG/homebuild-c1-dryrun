using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C9 Lab 7's end-to-end path, opt-in (HOMEBUILD_AWS_TESTS=1, HOMEBUILD_TOPIC_ARN, HOMEBUILD_NOTIFICATIONS_BUCKET,
/// AWS_PROFILE, HomeBuild-Notifications deployed): a site manager completes a milestone; Construction's outbox relay
/// publishes MilestoneCompleted to SNS; homebuild-notifications delivers it to the Lambda, which writes the object to S3;
/// My Home's GET /me/notifications reads it back.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class RealNotificationsTests(PostgresFixture database)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Completed_milestone_reaches_my_home_through_the_lambda()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1, HOMEBUILD_TOPIC_ARN and HOMEBUILD_NOTIFICATIONS_BUCKET.");
        var topic = Environment.GetEnvironmentVariable("HOMEBUILD_TOPIC_ARN") ?? throw new InvalidOperationException("Set HOMEBUILD_TOPIC_ARN.");
        var bucket = Environment.GetEnvironmentVariable("HOMEBUILD_NOTIFICATIONS_BUCKET") ?? throw new InvalidOperationException("Set HOMEBUILD_NOTIFICATIONS_BUCKET.");
        await using var api = await database.CreateApiAsync();
        await using var factory = api.WithWebHostBuilder(b => b
            .UseSetting("Messaging:TopicArn", topic)
            .UseSetting("Messaging:RelayEnabled", "true")
            .UseSetting("Messaging:RelayInterval", "00:00:01")
            .UseSetting("Notifications:BucketName", bucket));
        using var client = factory.CreateClient();
        var buyer = TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer");
        var siteManager = TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager");
        var clock = Stopwatch.StartNew();

        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{EndpointTests.RiversidePlot1}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) })
        {
            reserve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(reserve, Ct)).StatusCode);
        }

        using (var complete = new HttpRequestMessage(HttpMethod.Post, $"/v2/units/{EndpointTests.RiversidePlot1}/milestones/Frame/completion")
        {
            Content = JsonContent.Create(new { completedOn = DateOnly.FromDateTime(DateTime.UtcNow) }),
        })
        {
            complete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", siteManager);
            using var completed = await client.SendAsync(complete, Ct);
            Assert.True(completed.IsSuccessStatusCode, $"complete Frame: {completed.StatusCode} {await completed.Content.ReadAsStringAsync(Ct)}");
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s Frame completed for Riverside plot 1 (reserved by the buyer)");
        while (true)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(2), "no notification within 2 minutes");
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/me/notifications");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer);
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(Ct);
            if (JsonElement.Parse(body).EnumerateArray().Any(n => n.GetProperty("text").GetString()!.Contains("(Frame)", StringComparison.Ordinal)))
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s GET /v2/me/notifications: {body}");
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }
    }
}
