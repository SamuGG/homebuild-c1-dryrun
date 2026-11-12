using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Tests;

/// <summary>The brief's access table, one test case per row and caller.</summary>
public sealed class AuthorizationTests(PostgresFixture database) : IAsyncLifetime
{
    private static readonly Guid Owner = TestTokens.Users.Owner;
    private static readonly Guid OtherBuyer = TestTokens.Users.OtherBuyer;
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _reservation = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, HttpStatusCode> Table => new()
    {
        // Create or edit developments and units: admin
        { "create-development", "admin", HttpStatusCode.Created },
        { "create-development", "sales-agent", HttpStatusCode.Forbidden },
        { "create-development", "site-manager", HttpStatusCode.Forbidden },
        { "create-development", "finance", HttpStatusCode.Forbidden },
        { "create-development", "aftercare", HttpStatusCode.Forbidden },
        { "create-development", "buyer", HttpStatusCode.Forbidden },
        { "edit-development", "admin", HttpStatusCode.NoContent },
        { "edit-development", "sales-agent", HttpStatusCode.Forbidden },
        { "edit-development", "buyer", HttpStatusCode.Forbidden },
        { "edit-unit", "admin", HttpStatusCode.NoContent },
        { "edit-unit", "sales-agent", HttpStatusCode.Forbidden },
        { "edit-unit", "site-manager", HttpStatusCode.Forbidden },
        { "edit-unit", "buyer", HttpStatusCode.Forbidden },
        // Reserve a unit: buyer (for themselves) or sales-agent (for a named buyer)
        { "reserve-self", "buyer", HttpStatusCode.Created },
        { "reserve-for-other", "buyer", HttpStatusCode.Forbidden },
        { "reserve-for-other", "sales-agent", HttpStatusCode.Created },
        { "reserve-self", "admin", HttpStatusCode.Forbidden },
        { "reserve-self", "site-manager", HttpStatusCode.Forbidden },
        { "reserve-self", "finance", HttpStatusCode.Forbidden },
        { "reserve-self", "aftercare", HttpStatusCode.Forbidden },
        // Read a reservation: the owning buyer, sales-agent, finance, admin
        { "read", "owner", HttpStatusCode.OK },
        { "read", "other-buyer", HttpStatusCode.NotFound },
        { "read", "sales-agent", HttpStatusCode.OK },
        { "read", "finance", HttpStatusCode.OK },
        { "read", "admin", HttpStatusCode.OK },
        { "read", "site-manager", HttpStatusCode.Forbidden },
        { "read", "aftercare", HttpStatusCode.Forbidden },
        // Cancel a reservation: the owning buyer before deposit; sales-agent, admin
        { "cancel", "owner", HttpStatusCode.NoContent },
        { "cancel-after-deposit", "owner", HttpStatusCode.Forbidden },
        { "cancel-after-deposit", "sales-agent", HttpStatusCode.NoContent },
        { "cancel", "other-buyer", HttpStatusCode.NotFound },
        { "cancel", "sales-agent", HttpStatusCode.NoContent },
        { "cancel", "admin", HttpStatusCode.NoContent },
        { "cancel", "finance", HttpStatusCode.Forbidden },
        { "cancel", "site-manager", HttpStatusCode.Forbidden },
        { "cancel", "aftercare", HttpStatusCode.Forbidden },
        // Read deposits and payments: finance, admin, the owning buyer
        { "read-deposit", "finance", HttpStatusCode.OK },
        { "read-deposit", "admin", HttpStatusCode.OK },
        { "read-deposit", "owner", HttpStatusCode.OK },
        { "read-deposit", "other-buyer", HttpStatusCode.NotFound },
        { "read-deposit", "sales-agent", HttpStatusCode.Forbidden },
        { "read-deposit", "site-manager", HttpStatusCode.Forbidden },
        { "read-deposit", "aftercare", HttpStatusCode.Forbidden },
    };

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _client = _factory.CreateClient();
        // A reservation owned by Owner, on Riverside plot 1.
        var created = await SendAsync(HttpMethod.Post, $"/v1/units/{EndpointTests.RiversidePlot1}/reservations", "owner",
            new { deposit = 5000, currency = "GBP" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _reservation = created.Headers.Location!.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Table))]
    public async Task Access_table_is_enforced(string action, string caller, HttpStatusCode expected)
    {
        var response = action switch
        {
            "create-development" => await SendAsync(HttpMethod.Post, "/v1/developments", caller,
                new { name = "New", town = "Hull", depositStrategy = "fixed" }),
            "edit-development" => await SendAsync(HttpMethod.Put, $"/v1/developments/{Riverside}", caller,
                new { name = "Riverside Quay", town = "York" }),
            "edit-unit" => await SendAsync(HttpMethod.Put, $"/v2/units/{EndpointTests.RiversidePlot3}", caller,
                new { price = 285000, description = "Corner plot" }),
            "reserve-self" => await SendAsync(HttpMethod.Post, $"/v1/units/{EndpointTests.RiversidePlot2}/reservations", caller,
                new { deposit = 5000, currency = "GBP" }),
            "reserve-for-other" => await SendAsync(HttpMethod.Post, $"/v1/units/{EndpointTests.RiversidePlot2}/reservations", caller,
                new { buyerId = OtherBuyer, deposit = 5000, currency = "GBP" }),
            "read" => await SendAsync(HttpMethod.Get, _reservation, caller),
            "cancel" => await SendAsync(HttpMethod.Delete, _reservation, caller),
            "cancel-after-deposit" => await CancelAfterDepositAsync(caller),
            "read-deposit" => await SendAsync(HttpMethod.Get, _reservation + "/deposit", caller),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        Assert.True(expected == response.StatusCode, $"{action} as {caller}: expected {expected}, got {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        if ((int)expected >= 400)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Buyer_cannot_read_other_buyers_reservation_returns_404()
    {
        var other = await SendAsync(HttpMethod.Get, _reservation, "other-buyer");
        var missing = await SendAsync(HttpMethod.Get, "/v1/reservations/0198a000-0000-7000-8000-0000000000ff", "other-buyer");

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        // Indistinguishable from a reservation that does not exist.
        Assert.Equal(missing.StatusCode, other.StatusCode);
        Assert.Equal(Title(await missing.Content.ReadAsStringAsync(Ct)), Title(await other.Content.ReadAsStringAsync(Ct)));
    }

    private async Task<HttpResponseMessage> CancelAfterDepositAsync(string caller)
    {
        await using (var db = SalesModule.CreateDbContext(_factory.ConnectionString, TestTenants.Demo))
        {
            var id = new ReservationId(Guid.Parse(_reservation.Split('/')[^1]));
            var reservation = await db.Reservations.SingleAsync(r => r.Id == id, Ct);
            Assert.IsType<Transitioned>(reservation.Confirm(DateTimeOffset.UtcNow).Value);
            await db.SaveChangesAsync(Ct);
        }

        return await SendAsync(HttpMethod.Delete, _reservation, caller);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string caller, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller switch
        {
            "owner" => TestTokens.Access(Owner.ToString(), "buyer"),
            "other-buyer" => TestTokens.Access(OtherBuyer.ToString(), "buyer"),
            "buyer" => TestTokens.Access(TestTokens.Users.ThirdBuyer.ToString(), "buyer"), // neither Owner nor OtherBuyer
            _ => TestTokens.Access(TestTokens.Users.Staff(caller).ToString(), caller),
        });
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, Ct);
    }

    private static string? Title(string body) => body.Length == 0 ? null : JsonElement.Parse(body).GetProperty("title").GetString();
}
