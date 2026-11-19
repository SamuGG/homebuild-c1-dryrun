using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Data;
using HomeBuild.Api.Modules.Extras;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 1's acceptance tests: extras open and close as MilestoneCompleted events arrive on homebuild-extras. Each
/// event is handed to the consumer as the queue would deliver it (CI has no SQS).
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class ExtrasTests(PostgresFixture database)
{
    private static readonly Guid Unit = Guid.Parse(EndpointTests.RiversidePlot1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Extras_open_when_unlock_milestone_completes()
    {
        await using var api = await database.CreateApiAsync();

        await DeliverAsync(api, Milestone.Frame, version: 2);

        Assert.Equal(["Designer kitchen", "Extra parking space", "Premium kitchen", "Smart home package", "Solar panels"], await OpenAsync(api));
        var unlocked = Assert.Single(await UnlockedAsync(api));
        Assert.Equal("Frame", unlocked.Milestone);
        // The unit's first event: everything open after Frame is new to Extras, including solar and parking (open since Foundations).
        Assert.Equal(["Designer kitchen", "Extra parking space", "Premium kitchen", "Smart home package", "Solar panels"], unlocked.Extras.Select(e => e.Name));
    }

    [Fact]
    public async Task Extras_close_when_close_milestone_completes()
    {
        await using var api = await database.CreateApiAsync();
        await DeliverAsync(api, Milestone.Frame, version: 2);

        await DeliverAsync(api, Milestone.FirstFix, version: 4);

        var open = await OpenAsync(api);
        Assert.DoesNotContain("Premium kitchen", open);
        Assert.DoesNotContain("Smart home package", open);
        Assert.Equal(["Engineered oak flooring", "Extra parking space"], open);
    }

    // A redelivered (or relayed-twice) Frame after FirstFix: its version is older, so the kitchens stay closed.
    [Fact]
    public async Task Duplicate_milestone_event_does_not_reopen_closed_extras()
    {
        await using var api = await database.CreateApiAsync();
        await DeliverAsync(api, Milestone.Frame, version: 2);
        await DeliverAsync(api, Milestone.FirstFix, version: 4);

        await DeliverAsync(api, Milestone.Frame, version: 2);
        await DeliverAsync(api, Milestone.FirstFix, version: 4);

        Assert.DoesNotContain("Premium kitchen", await OpenAsync(api));
        Assert.Equal(["Frame", "FirstFix"], (await UnlockedAsync(api)).Select(u => u.Milestone)); // FirstFix opened the flooring; the duplicates wrote nothing
    }

    // The Break, kept: the close milestone arrives before the unlock milestone (SNS/SQS do not keep order). The version
    // check ignores the older unlock, so the kitchens never open.
    [Fact]
    public async Task Close_milestone_before_unlock_keeps_extra_closed()
    {
        await using var api = await database.CreateApiAsync();

        await DeliverAsync(api, Milestone.FirstFix, version: 4);
        await DeliverAsync(api, Milestone.Frame, version: 2);

        Assert.DoesNotContain("Premium kitchen", await OpenAsync(api));
        Assert.DoesNotContain(await UnlockedAsync(api), u => u.Milestone == "Frame");
    }

    // Every milestone, in turn: an extra is open exactly from its unlock milestone until its close milestone.
    public static TheoryData<Milestone> EveryMilestone => [.. BuildProgress.Order];

    [Theory]
    [MemberData(nameof(EveryMilestone))]
    public async Task Extra_is_never_open_before_unlock_or_after_close(Milestone latest)
    {
        await using var api = await database.CreateApiAsync();

        await DeliverAsync(api, latest, version: (int)latest + 1);

        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var catalogue = await db.Extras.AsNoTracking().Where(e => e.DevelopmentId == SeedData.Riverside).ToListAsync(Ct);
        var open = await OpenAsync(api);
        foreach (var extra in catalogue)
        {
            var shouldBeOpen = latest >= extra.UnlocksAt && latest < extra.ClosesAt;
            Assert.True(shouldBeOpen == open.Contains(extra.Name), $"{extra.Name} ({extra.UnlocksAt}→{extra.ClosesAt}) after {latest}: open={open.Contains(extra.Name)}");
        }
    }

    [Fact]
    public async Task Buyer_sees_open_extras_and_staff_see_every_window()
    {
        await using var api = await database.CreateApiAsync();
        using var client = api.CreateClient();
        await DeliverAsync(api, Milestone.Frame, version: 2);
        var buyer = TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer");
        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{Unit}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) })
        {
            reserve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(reserve, Ct)).StatusCode);
        }

        var mine = await GetAsync(client, "/v2/me/extras", buyer);
        var home = Assert.Single(mine.EnumerateArray());
        Assert.Equal("Frame", home.GetProperty("latestMilestone").GetString());
        Assert.Equal(5, home.GetProperty("openExtras").GetArrayLength());

        var staff = await GetAsync(client, $"/v2/developments/{SeedData.Riverside.Value}/extras", TestTokens.Access(TestTokens.Users.Staff("sales-agent").ToString(), "sales-agent"));
        Assert.Equal(7, staff.GetArrayLength());
        Assert.Contains(staff.EnumerateArray(), e => e.GetProperty("name").GetString() == "Premium kitchen" && e.GetProperty("unlocksAt").GetString() == "Frame" && e.GetProperty("closesAt").GetString() == "FirstFix");

        using var forbidden = new HttpRequestMessage(HttpMethod.Get, $"/v2/developments/{SeedData.Riverside.Value}/extras");
        forbidden.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forbidden, Ct)).StatusCode);
    }

    /// <summary>Hands a MilestoneCompleted for Riverside plot 1 to the consumer, as homebuild-extras would.</summary>
    private static async Task DeliverAsync(WebApplicationFactory<Program> api, Milestone milestone, int version)
    {
        var body = JsonSerializer.Serialize(new MilestoneCompletedMessage(Unit, TestTenants.Demo, milestone.ToString(), new DateOnly(2026, 11, 19), version, SeedData.Riverside.Value), JsonSerializerOptions.Web);
        var message = new QueueMessage(Guid.NewGuid().ToString(), "receipt", body,
            new Dictionary<string, string> { ["type"] = IntegrationEvents.MilestoneCompleted, ["message_id"] = Guid.NewGuid().ToString() }, 1);
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredKeyedService<IMessageHandler>(ExtrasProgressConsumer.Name).HandleAsync(message, Ct);
    }

    private static async Task<List<string>> OpenAsync(ApiFactory api)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var progress = await db.UnitProgress.AsNoTracking().SingleAsync(p => p.UnitId == new UnitId(Unit), Ct);
        return [.. (await db.Extras.AsNoTracking().Where(e => e.DevelopmentId == progress.DevelopmentId).ToListAsync(Ct))
            .Where(e => e.Window.IsOpenAfter(progress.LatestMilestone)).Select(e => e.Name).Order()];
    }

    private static async Task<List<ExtrasUnlockedMessage>> UnlockedAsync(ApiFactory api)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        return [.. (await db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Type == IntegrationEvents.ExtrasUnlocked).OrderBy(m => m.OccurredAt).ToListAsync(Ct))
            .Select(m => JsonSerializer.Deserialize<ExtrasUnlockedMessage>(m.Payload, JsonSerializerOptions.Web)!)];
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
    }
}
