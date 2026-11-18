using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using HomeBuild.Api.Data;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>
/// Opt-in (HOMEBUILD_AWS_TESTS=1, with HOMEBUILD_TOPIC_ARN and AWS_PROFILE set), like C5's real-S3 tests: the real relays
/// publish through the real SNS adapter to homebuild-events, and the test reads every queue to check the filter
/// policies. CI never sets the variable. It deletes what it reads.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class RealMessagingTests(PostgresFixture database)
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";
    private static readonly string[] Queues = ["contracts", "payments", "notifications", "cache-invalidation"];

    [Fact]
    public async Task Relays_publish_each_event_type_to_the_queues_that_handle_it()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1 and HOMEBUILD_TOPIC_ARN.");
        var ct = TestContext.Current.CancellationToken;
        var topic = Environment.GetEnvironmentVariable("HOMEBUILD_TOPIC_ARN") ?? throw new InvalidOperationException("Set HOMEBUILD_TOPIC_ARN.");
        await using var api = await database.CreateApiAsync();
        await using var factory = api.WithWebHostBuilder(b => b.UseSetting("Messaging:TopicArn", topic).UseSetting("Messaging:RelayEnabled", "false"));
        var at = DateTimeOffset.UtcNow;
        var ids = new Dictionary<string, Guid>();
        await using (var db = PaymentsModule.CreateDbContext(api.ConnectionString, TestTenants.Demo))
        {
            var m = OutboxMessage.For(IntegrationEvents.DepositPaid, new DepositPaidMessage(Guid.NewGuid(), Guid.NewGuid(), TestTenants.Demo, 5000m, "GBP", "cs_c9_lab2", at), TestTenants.Demo, at);
            db.Set<OutboxMessage>().Add(m);
            await db.SaveChangesAsync(ct);
            ids[m.Type] = m.Id;
        }

        await using (var db = ConstructionModule.CreateDbContext(api.ConnectionString, TestTenants.Demo))
        {
            var m = OutboxMessage.For(IntegrationEvents.MilestoneCompleted, new MilestoneCompletedMessage(Guid.NewGuid(), TestTenants.Demo, "Roof", DateOnly.FromDateTime(at.UtcDateTime)), TestTenants.Demo, at);
            db.Set<OutboxMessage>().Add(m);
            await db.SaveChangesAsync(ct);
            ids[m.Type] = m.Id;
        }

        await using (var db = SalesModule.CreateDbContext(api.ConnectionString, TestTenants.Demo))
        {
            var m = OutboxMessage.For(IntegrationEvents.ReservationExpired, new ReservationExpiredMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TestTenants.Demo, at), TestTenants.Demo, at);
            db.Set<OutboxMessage>().Add(m);
            await db.SaveChangesAsync(ct);
            ids[m.Type] = m.Id;
        }

        Assert.Equal(1, await factory.Services.GetRequiredService<OutboxRelay<PaymentsDbContext>>().RelayOnceAsync(ct));
        Assert.Equal(1, await factory.Services.GetRequiredService<OutboxRelay<ConstructionDbContext>>().RelayOnceAsync(ct));
        Assert.Equal(1, await factory.Services.GetRequiredService<OutboxRelay<SalesDbContext>>().RelayOnceAsync(ct));

        using var sqs = new AmazonSQSClient(RegionEndpoint.EUWest2);
        var received = new Dictionary<string, List<string>>();
        foreach (var queue in Queues)
        {
            var url = (await sqs.GetQueueUrlAsync($"homebuild-{queue}", ct)).QueueUrl;
            received[queue] = [];
            for (var poll = 0; poll < 3; poll++)
            {
                var batch = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, WaitTimeSeconds = 5, MaxNumberOfMessages = 10, MessageAttributeNames = ["All"] }, ct);
                foreach (var message in batch.Messages ?? [])
                {
                    if (message.MessageAttributes.TryGetValue("message_id", out var id) && ids.ContainsValue(Guid.Parse(id.StringValue)))
                    {
                        received[queue].Add(message.MessageAttributes["type"].StringValue);
                        await sqs.DeleteMessageAsync(url, message.ReceiptHandle, ct);
                    }
                }
            }
        }

        foreach (var (queue, types) in received)
        {
            TestContext.Current.TestOutputHelper!.WriteLine($"homebuild-{queue}: {string.Join(", ", types.Order())}");
        }

        Assert.Equal(["DepositPaid"], received["contracts"].Order());
        Assert.Equal(["DepositPaid", "MilestoneCompleted", "ReservationExpired"], received["payments"].Order());
        Assert.Equal(["MilestoneCompleted", "ReservationExpired"], received["notifications"].Order());
        Assert.Equal(["ReservationExpired"], received["cache-invalidation"].Order());
    }
}
