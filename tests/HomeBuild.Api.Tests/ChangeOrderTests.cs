using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HomeBuild.Api.Data;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Extras;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 3's acceptance tests: the change-order worker (a consumer of ExtrasPaid on homebuild-change-orders), the site
/// manager's actions, and the states a buyer sees. ExtrasPaid is handed to the consumer as the queue would deliver it.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class ChangeOrderTests(PostgresFixture database)
{
    private static readonly Guid Unit = Guid.Parse(EndpointTests.RiversidePlot1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Paid_extras_create_one_change_order()
    {
        await using var api = await database.CreateApiAsync();
        var paid = await PaidBasketAsync(api, ("Premium kitchen", 6_500m, "FirstFix"), ("Extra parking space", 9_000m, "SecondFix"));

        await DeliverAsync(api, paid);

        await using var db = ConstructionModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var orders = await db.ChangeOrders.AsNoTracking().OrderBy(c => c.ExtraName).ToListAsync(Ct);
        Assert.Equal(["Extra parking space", "Premium kitchen"], orders.Select(o => o.ExtraName));
        Assert.Equal([Milestone.SecondFix, Milestone.FirstFix], orders.Select(o => o.FitBy));
        Assert.All(orders, o => Assert.Equal((ChangeOrderStatus.Created, new UnitId(Unit), SeedData.Riverside), (o.Status, o.UnitId, o.DevelopmentId)));
        Assert.Equal(2, await db.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.ChangeOrderCreated, Ct));
    }

    // Redelivered, relayed twice, and five copies at once: one change order per paid line, one ChangeOrderCreated each.
    [Fact]
    public async Task Change_order_creation_is_idempotent()
    {
        await using var api = await database.CreateApiAsync();
        var paid = await PaidBasketAsync(api, ("Premium kitchen", 6_500m, "FirstFix"));

        await DeliverAsync(api, paid);
        await DeliverAsync(api, paid);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => DeliverAsync(api, paid)));

        await using var db = ConstructionModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        Assert.Equal(1, await db.ChangeOrders.CountAsync(Ct));
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync(m => m.Type == IntegrationEvents.ChangeOrderCreated, Ct));
    }

    // The buyer's state moves only when the system does: Paid (webhook processed), Ordered on site (the worker created
    // the change order), Fitted (the site manager said so). Staff and buyer read different DTOs.
    [Fact]
    public async Task Buyer_sees_each_state_only_after_it_is_reached()
    {
        await using var api = await database.CreateApiAsync();
        using var client = api.CreateClient();
        var paid = await PaidBasketAsync(api, ("Premium kitchen", 6_500m, "FirstFix"));
        var buyer = TestTokens.Access(TestTokens.Users.Owner.ToString(), "buyer");

        Assert.Equal("Paid", await StateAsync(client, buyer));
        await DeliverAsync(api, paid);
        Assert.Equal("Ordered on site", await StateAsync(client, buyer));

        var siteManager = TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager");
        var order = (await GetAsync(client, $"/v2/developments/{SeedData.Riverside.Value}/change-orders", siteManager)).EnumerateArray().Single();
        Assert.Equal(("Premium kitchen", "FirstFix", "Created"), (order.GetProperty("extraName").GetString(), order.GetProperty("fitBy").GetString(), order.GetProperty("status").GetString()));
        var id = order.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, await PostAsync(client, $"/v2/change-orders/{id}/fitting", siteManager)); // acknowledge first
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync(client, $"/v2/change-orders/{id}/acknowledgement",
            TestTokens.Access(TestTokens.Users.HillcrestSiteManager.ToString(), "site-manager"))); // not assigned to Riverside
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, $"/v2/change-orders/{id}/acknowledgement", siteManager));
        Assert.Equal("Ordered on site", await StateAsync(client, buyer));
        Assert.Equal(HttpStatusCode.OK, await PostAsync(client, $"/v2/change-orders/{id}/fitting", siteManager));
        Assert.Equal("Fitted", await StateAsync(client, buyer));

        var buyerLine = (await GetAsync(client, "/v2/me/baskets", buyer))[0].GetProperty("lines")[0];
        Assert.False(buyerLine.TryGetProperty("acknowledgedBy", out _)); // the staff fields never reach the buyer's DTO
    }

    /// <summary>A paid basket for the owner and Riverside plot 1, and the ExtrasPaid it would have produced.</summary>
    private static async Task<ExtrasPaidMessage> PaidBasketAsync(ApiFactory api, params (string Name, decimal Price, string FitBy)[] lines)
    {
        await using var db = ExtrasModule.CreateDbContext(api.ConnectionString, TestTenants.Demo);
        var extras = await db.Extras.AsNoTracking().Where(e => e.DevelopmentId == SeedData.Riverside).ToDictionaryAsync(e => e.Name, Ct);
        var basket = new BasketRow
        {
            BuyerId = new BuyerId(TestTokens.Users.Owner), UnitId = new UnitId(Unit), DevelopmentId = SeedData.Riverside, CreatedAt = DateTimeOffset.UtcNow,
            Status = BasketStatus.Paid, SessionId = "cs_test_" + Guid.NewGuid().ToString("N"), PaymentIntent = "pi_test", ChargedAmount = lines.Sum(l => l.Price), PaidAt = DateTimeOffset.UtcNow,
            Lines = [.. lines.Select(l => new BasketLineRow { ExtraId = extras[l.Name].Id, Name = l.Name, UnitPrice = l.Price, Currency = "GBP", AddedAt = DateTimeOffset.UtcNow, Status = BasketLineStatus.Paid })],
        };
        db.Baskets.Add(basket);
        await db.SaveChangesAsync(Ct);
        return new ExtrasPaidMessage(basket.Id, Unit, TestTenants.Demo, TestTokens.Users.Owner,
            [.. basket.Lines.Zip(lines).Select(z => new PaidExtra(z.First.Id, z.First.ExtraId, z.First.Name, z.First.UnitPrice, "GBP", 1, z.Second.FitBy))],
            lines.Sum(l => l.Price), "GBP", DateTimeOffset.UtcNow);
    }

    private static async Task DeliverAsync(WebApplicationFactory<Program> api, ExtrasPaidMessage paid)
    {
        var message = new QueueMessage(Guid.NewGuid().ToString(), "receipt", JsonSerializer.Serialize(paid, JsonSerializerOptions.Web),
            new Dictionary<string, string> { ["type"] = IntegrationEvents.ExtrasPaid, ["message_id"] = Guid.NewGuid().ToString() }, 1);
        await using var scope = api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredKeyedService<IMessageHandler>(ChangeOrdersConsumer.Name).HandleAsync(message, Ct);
    }

    private static async Task<string?> StateAsync(HttpClient client, string token) =>
        (await GetAsync(client, "/v2/me/baskets", token))[0].GetProperty("lines")[0].GetProperty("state").GetString();

    private static async Task<JsonElement> GetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }
}
