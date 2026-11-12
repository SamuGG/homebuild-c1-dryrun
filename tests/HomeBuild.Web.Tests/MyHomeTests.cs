using HomeBuild.Web.Auth;

namespace HomeBuild.Web.Tests;

public sealed class MyHomeTests
{
    [Fact]
    public async Task My_home_is_static_and_shows_homes_progress_and_documents_placeholder()
    {
        var api = new FakeCatalogueApi();
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)), "buyer");

        var html = await browser.GetStringAsync(new Uri("/my-home", UriKind.Relative), TestContext.Current.CancellationToken);
        var page = HtmlForm.Parse(html);

        Assert.Equal("Plot P1, Riverside", page.QuerySelector("[data-testid=homes] h2")!.TextContent);
        var bar = page.QuerySelector("[data-testid=homes] progress")!;
        Assert.Equal(("1", "3"), (bar.GetAttribute("value"), bar.GetAttribute("max")));
        Assert.Contains("1 of 3 steps done; next: Roof", html, StringComparison.Ordinal);
        Assert.NotNull(page.QuerySelector("[data-testid=documents]"));
        Assert.DoesNotContain("<!--Blazor:{", html, StringComparison.Ordinal); // static SSR: no circuit
        Assert.Contains(api.Requests, r => r.RequestUri!.AbsolutePath == "/v2/me/progress");
        Assert.DoesNotContain(api.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/board", StringComparison.Ordinal));
    }
}
