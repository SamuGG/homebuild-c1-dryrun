using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Modules.Notifications;
using HomeBuild.Domain;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>C9 Lab 7: My Home reads the notifications the Lambda wrote (S3 replaced by an in-memory reader).</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class MyNotificationsTests(PostgresFixture database)
{
    private static readonly Guid Plot1 = Guid.Parse(EndpointTests.RiversidePlot1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Buyer_sees_their_own_and_their_units_notifications_newest_first()
    {
        var buyer = TestTokens.Users.Owner;
        var other = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var reader = new MemoryReader(new()
        {
            [$"notifications/{TestTenants.Demo}/{buyer}/"] = [Stored("ReservationExpired", Plot1, "Your reservation hold has ended", now.AddMinutes(-5), buyer)],
            [$"notifications/{TestTenants.Demo}/units/{Plot1}/"] = [Stored("MilestoneCompleted", Plot1, "Good news: Frame", now)],
            [$"notifications/{TestTenants.Demo}/units/{other}/"] = [Stored("MilestoneCompleted", other, "Not this buyer's unit", now)],
        });
        await using var api = await database.CreateApiAsync();
        await using var factory = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<INotificationReader>(reader)));
        using var client = factory.CreateClient();
        using (var reserve = new HttpRequestMessage(HttpMethod.Post, $"/v1/units/{Plot1}/reservations") { Content = JsonContent.Create(new { deposit = 5000m, currency = "GBP" }) })
        {
            reserve.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(buyer.ToString(), "buyer"));
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(reserve, Ct)).StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/me/notifications");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(buyer.ToString(), "buyer"));
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var texts = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct)).EnumerateArray().Select(n => n.GetProperty("text").GetString()).ToList();
        Assert.Equal(["Good news: Frame", "Your reservation hold has ended"], texts);
        Assert.Equal(2, reader.Prefixes.Count); // the buyer's prefix and the one unit they hold
    }

    private static StoredNotification Stored(string type, Guid unit, string text, DateTimeOffset at, Guid? buyer = null) =>
        new(Guid.NewGuid(), type, TestTenants.Demo, unit, null, buyer, text, at, null);

    private sealed class MemoryReader(Dictionary<string, List<StoredNotification>> objects) : INotificationReader
    {
        public List<string> Prefixes { get; } = [];

        public Task<IReadOnlyList<StoredNotification>> ReadAsync(string prefix, int max, CancellationToken cancellationToken)
        {
            Prefixes.Add(prefix);
            return Task.FromResult<IReadOnlyList<StoredNotification>>(objects.GetValueOrDefault(prefix) ?? []);
        }
    }
}
