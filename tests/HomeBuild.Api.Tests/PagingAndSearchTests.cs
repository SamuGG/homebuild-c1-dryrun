using static HomeBuild.Testing.Builders;
using System.Net;
using System.Text.Json;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;

namespace HomeBuild.Api.Tests;

public sealed class PagingAndSearchTests(PostgresFixture database)
{
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";
    private static readonly DevelopmentId RiversideId = new(Guid.Parse(Riverside));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Paging_returns_every_unit_exactly_once_while_rows_are_inserted()
    {
        var cs = await database.CreateDatabaseAsync();
        await AddUnitsAsync(cs, Enumerable.Range(0, 30).Select(i => 300_000m + ((i % 5) * 1_000m))); // many equal prices
        var original = await UnitIdsInRiversideAsync(cs);
        await using var factory = new ApiFactory(cs);
        using var client = factory.CreateClient();

        var seen = new List<Guid>();
        string? next = null;
        var pages = 0;
        do
        {
            var url = $"/v2/developments/{Riverside}/units?size=7" + (next is null ? "" : $"&after={Uri.EscapeDataString(next)}");
            var page = JsonElement.Parse(await client.GetStringAsync(url, Ct));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            next = page.GetProperty("next").GetString();
            pages++;
            // Other users add units while we page: one cheaper than anything seen, one in the middle of the range.
            await AddUnitsAsync(cs, [100_000m + pages, 300_500m + pages]);
        }
        while (next is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count()); // nothing repeated
        Assert.All(original, id => Assert.Contains(id, seen)); // nothing that existed at the start was skipped
        Assert.True(pages >= 5, $"{pages} pages"); // 33 units at the start, 7 per page
    }

    [Fact]
    public async Task Cursor_is_opaque_and_tamper_evident()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();
        var first = JsonElement.Parse(await client.GetStringAsync($"/v2/developments/{Riverside}/units?size=1", Ct));
        var cursor = first.GetProperty("next").GetString()!;
        var lastId = first.GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.DoesNotContain(lastId.ToString(), cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("260000", cursor, StringComparison.Ordinal);
        var tampered = cursor[..^3] + (cursor[^3] == 'A' ? 'B' : 'A') + cursor[^2..];
        foreach (var bad in new[] { tampered, "MjYwMDAwfDAxOThhMDAwLTAwMDAtNzAwMC04MDAxLTAwMDAwMDAwMDAwMQ" })
        {
            var response = await client.GetAsync($"/v2/developments/{Riverside}/units?size=1&after={Uri.EscapeDataString(bad)}", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var ok = await client.GetAsync($"/v2/developments/{Riverside}/units?size=1&after={Uri.EscapeDataString(cursor)}", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Search_matches_stemmed_words()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();

        // Descriptions say "a south-facing balcony" and "views over the river"; the queries use other forms of the words.
        var balconies = JsonElement.Parse(await client.GetStringAsync("/v1/units/search?q=balconies", Ct)).GetProperty("items");
        var viewing = JsonElement.Parse(await client.GetStringAsync("/v1/units/search?q=viewing%20rivers", Ct)).GetProperty("items");
        var none = JsonElement.Parse(await client.GetStringAsync("/v1/units/search?q=penthouse", Ct)).GetProperty("items");

        Assert.Equal(4, balconies.GetArrayLength()); // plots 1 and 3 in both developments have balconies
        Assert.Equal(3, viewing.GetArrayLength()); // Riverside's three plots
        Assert.All(viewing.EnumerateArray(), h => Assert.StartsWith("Riverside", h.GetProperty("name").GetString(), StringComparison.Ordinal));
        Assert.Equal(0, none.GetArrayLength());
    }

    private static async Task AddUnitsAsync(string cs, IEnumerable<decimal> prices)
    {
        await using var db = CatalogueModule.CreateDbContext(cs, TestTenants.Demo);
        foreach (var price in prices)
        {
            db.Units.Add(AUnit().InDevelopment(RiversideId).WithPlot("N" + Guid.NewGuid().ToString("N")[..6]).PricedAt(new Money(price, "GBP")).Build());
        }

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<List<Guid>> UnitIdsInRiversideAsync(string cs)
    {
        await using var db = CatalogueModule.CreateDbContext(cs, TestTenants.Demo);
        return [.. db.Units.Where(u => u.DevelopmentId == RiversideId).Select(u => u.Id).AsEnumerable().Select(id => id.Value)];
    }
}
