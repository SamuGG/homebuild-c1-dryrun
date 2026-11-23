using System.Diagnostics;
using Amazon;
using Amazon.S3;
using Amazon.SQS;
using static HomeBuild.Api.Tests.AftercareTests;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 6 end to end, opt-in (HOMEBUILD_AWS_TESTS=1, HOMEBUILD_TOPIC_ARN, HOMEBUILD_NOTIFICATIONS_BUCKET, AWS_PROFILE;
/// HomeBuild-Scheduling, HomeBuild-Messaging and HomeBuild-Notifications deployed, the Lambda with -c emailFrom=…):
/// Aftercare schedules a visit for tomorrow, so the buyer's reminder (09:00 today, already past) is due a minute from
/// now. A real one-time schedule fires to homebuild-scheduled, this API's consumer re-checks the snag and writes
/// SnagReminderDue to Aftercare's outbox, the relay publishes it, and the notifications Lambda sends it through SES and
/// leaves its send-once claim under emails/. The email itself is checked in the inbox (a screenshot in the PR).
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class RealSnagReminderTests(PostgresFixture database)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Visit_reminder_is_emailed_through_ses()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1, HOMEBUILD_TOPIC_ARN and HOMEBUILD_NOTIFICATIONS_BUCKET.");
        var topic = Environment.GetEnvironmentVariable("HOMEBUILD_TOPIC_ARN") ?? throw new InvalidOperationException("Set HOMEBUILD_TOPIC_ARN.");
        var bucket = Environment.GetEnvironmentVariable("HOMEBUILD_NOTIFICATIONS_BUCKET") ?? throw new InvalidOperationException("Set HOMEBUILD_NOTIFICATIONS_BUCKET.");
        using var sqs = new AmazonSQSClient(RegionEndpoint.EUWest2);
        var queueUrl = (await sqs.GetQueueUrlAsync("homebuild-scheduled", Ct)).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"], Ct)).QueueARN;
        var account = queueArn.Split(':')[4];
        var (api, factory, client) = await StartAsync(database, b => b
            .UseSetting("Scheduling:QueueArn", queueArn)
            .UseSetting("Scheduling:QueueUrl", queueUrl)
            .UseSetting("Scheduling:RoleArn", $"arn:aws:iam::{account}:role/homebuild-scheduler")
            .UseSetting("Scheduling:Consumer:Wait", "00:00:05")
            .UseSetting("Messaging:TopicArn", topic)
            .UseSetting("Messaging:RelayEnabled", "true")
            .UseSetting("Messaging:RelayInterval", "00:00:01"));
        await using var _ = api;
        await using var __ = factory;
        using var ___ = client;
        var clock = Stopwatch.StartNew();
        void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s {text}");

        var id = await DraftAndReportAsync(client);
        await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/acknowledge", AftercareTests.Aftercare); // also deletes the acknowledgement schedule
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, HomeBuild.Domain.WorkingCalendar.London).DateTime).AddDays(1);
        await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/schedule", AftercareTests.Aftercare, new { subcontractor = "Northgate Plastering", visitOn = tomorrow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) });
        Log($"snag {id} scheduled for {tomorrow:yyyy-MM-dd}; schedule homebuild-snag-visit-{id} due in about a minute");

        using var s3 = new AmazonS3Client(RegionEndpoint.EUWest2);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        string? claim = null;
        var reminded = false;
        while (DateTimeOffset.UtcNow < deadline && claim is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            if (!reminded && (await SendAsync(client, HttpMethod.Get, "/v2/me/snags", Buyer)).ToString().Contains("Reminder sent: visit", StringComparison.Ordinal))
            {
                reminded = true;
                Log("timeline: Reminder sent (handler re-checked the snag and wrote SnagReminderDue)");
            }

            var listed = await s3.ListObjectsV2Async(new() { BucketName = bucket, Prefix = $"emails/{TestTenants.Demo}/" }, Ct);
            claim = listed.S3Objects?.FirstOrDefault(o => o.LastModified > DateTime.UtcNow.AddMinutes(-5))?.Key;
        }

        Log(claim is null ? "no email claim within 4 minutes" : $"Lambda sent the email: {claim}");
        Assert.True(reminded, "The reminder never reached the snag's timeline.");
        Assert.NotNull(claim);
    }
}
