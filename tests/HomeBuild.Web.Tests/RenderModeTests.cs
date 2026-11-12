using System.Net;
using System.Text;

namespace HomeBuild.Web.Tests;

public sealed class RenderModeTests
{
    public static TheoryData<string> CataloguePages =>
    [
        "/",
        $"/developments/{FakeCatalogueApi.Riverside}",
        $"/units/{FakeCatalogueApi.PlotOne}",
    ];

    [Theory]
    [MemberData(nameof(CataloguePages))]
    public async Task Catalogue_pages_render_without_websocket(string path)
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        // An interactive component leaves a <!--Blazor:{...}--> marker that tells blazor.web.js to open a
        // SignalR circuit (a WebSocket to /_blazor). Static SSR pages have none, so the browser opens no connection.
        Assert.DoesNotContain("<!--Blazor:{", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"server\"", html, StringComparison.Ordinal);
        Assert.Contains("<h1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_list_streams()
    {
        var api = new FakeCatalogueApi(holdUnits: true);
        await using var factory = new WebFactory(api);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Headers and the first chunk must arrive while the units call is still held. Without streaming rendering
        // the server waits for the data before sending anything, so this times out.
        var sending = client.GetAsync(new Uri("/", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await api.UnitsRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var arrived = await Task.WhenAny(sending, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
        Assert.True(arrived == sending, "No response arrived while the units call was pending: the page is not streaming.");
        using var response = await sending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        var first = await ReadUntilAsync(body, "Loading developments", cancellationToken);
        Assert.DoesNotContain("homes available", first, StringComparison.Ordinal);

        api.ReleaseUnits();
        var rest = await new StreamReader(body).ReadToEndAsync(cancellationToken);
        Assert.Contains("<blazor-ssr>", rest, StringComparison.Ordinal);
        Assert.Contains("1 of 1 homes available", rest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_development_is_not_found()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/developments/{Guid.CreateVersion7()}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pages_send_the_builder_as_tenant_header()
    {
        var api = new FakeCatalogueApi();
        await using var factory = new WebFactory(api);
        using var client = factory.CreateClient();

        await client.GetStringAsync(new Uri($"/units/{FakeCatalogueApi.PlotOne}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.All(api.Requests, r => Assert.Equal("0198a000-0000-7000-8000-00000000000a", Assert.Single(r.Headers.GetValues("X-Tenant-Id"))));
    }

    // Reads what has arrived so far, stopping once the marker is seen (without waiting for the end of the response).
    private static async Task<string> ReadUntilAsync(Stream body, string marker, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await body.ReadAsync(buffer, timeout.Token);
            Assert.True(read > 0, $"The response ended before '{marker}' arrived.");
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return text.ToString();
    }
}
