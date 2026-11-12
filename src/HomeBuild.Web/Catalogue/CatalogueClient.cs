using System.Net;
using System.Net.Http.Json;

namespace HomeBuild.Web.Catalogue;

public sealed record DevelopmentSummary(Guid Id, string Name, string Town);

public sealed record DevelopmentPage(IReadOnlyList<DevelopmentSummary> Items, int Page, int Size, int Total);

public sealed record Development(Guid Id, string Name, string Town);

public sealed record UnitSummary(Guid Id, string PlotNumber, decimal Price, string Currency, string Status);

public sealed record UnitPage(IReadOnlyList<UnitSummary> Items, string? Next);

public sealed record Price(decimal Amount, string Currency);

public sealed record Unit(Guid Id, Guid DevelopmentId, string PlotNumber, Price Price, string Status, string Description = "");

/// <summary>Options for the public catalogue API: where it is and which builder's catalogue this site shows.</summary>
public sealed class CatalogueApiOptions
{
    public const string Section = "CatalogueApi";

    /// <summary>The API's v2 root, ending in a slash, for example http://localhost:5104/v2/.</summary>
    public required Uri BaseAddress { get; set; }

    /// <summary>The builder (tenant) whose catalogue anonymous visitors browse; sent as X-Tenant-Id.</summary>
    public required Guid TenantId { get; set; }
}

/// <summary>Typed client for the API's anonymous catalogue reads (v2).</summary>
public sealed class CatalogueClient(HttpClient http)
{
    public const string TenantHeader = "X-Tenant-Id";

    public async Task<DevelopmentPage> ListDevelopmentsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<DevelopmentPage>("developments?page=1&size=100", cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty development page.");

    public Task<Development?> GetDevelopmentAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<Development>($"developments/{id}", cancellationToken);

    /// <summary>One keyset page of a development's units by price, or null if the development does not exist.</summary>
    public Task<UnitPage?> ListUnitsAsync(Guid developmentId, string? after = null, int size = 100, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<UnitPage>(
            $"developments/{developmentId}/units?size={size}" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}"),
            cancellationToken);

    public Task<Unit?> GetUnitAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetOrNullAsync<Unit>($"units/{id}", cancellationToken);

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
