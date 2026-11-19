using System.Net;
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

    // C10 Lab 1: the extras open now for a home are listed under it, with their price and what closes them.
    [Fact]
    public async Task My_home_lists_open_extras_per_home()
    {
        var api = new FakeCatalogueApi();
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)), "buyer");

        var html = await browser.GetStringAsync(new Uri("/my-home", UriKind.Relative), TestContext.Current.CancellationToken);
        var extras = HtmlForm.Parse(html).QuerySelector($"[data-testid=extras-{FakeCatalogueApi.PlotOne}]");

        Assert.NotNull(extras);
        Assert.Contains("Premium kitchen, £6,500", extras.TextContent, StringComparison.Ordinal);
        Assert.Contains("until FirstFix completes", extras.TextContent, StringComparison.Ordinal);
    }

    private static readonly Guid Held = Guid.Parse("0198a000-0000-7000-a000-000000000002");

    // C8 Lab 1: "Pay deposit" appears for held reservations only, and redirects to the Checkout page the API returns.
    [Fact]
    public async Task Pay_deposit_shows_for_held_reservations_and_redirects_to_the_session_url()
    {
        var api = new FakeCatalogueApi
        {
            Respond = r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
            {
                ("GET", "/v2/me/progress") => FakeCatalogueApi.Json(new HomeBuild.Web.Api.MyProgress(
                [
                    new(Guid.Parse("0198a000-0000-7000-a000-000000000001"), Guid.NewGuid(), "P1", "Riverside", "Confirmed", DateTimeOffset.UtcNow.AddDays(7), new("Frame", 1, 3, "Roof")),
                    new(Held, Guid.NewGuid(), "P2", "Riverside", "Held", DateTimeOffset.UtcNow.AddDays(7), new("Not started", 0, 3, "Foundations")),
                ])),
                ("POST", var path) when path == $"/v2/reservations/{Held}/deposit-session" => FakeCatalogueApi.Json(
                    new HomeBuild.Web.Api.DepositSession("cs_test_x", new Uri("https://checkout.stripe.com/c/pay/cs_test_x"), DateTimeOffset.UtcNow.AddHours(1))),
                _ => null,
            },
        };
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)), "buyer");
        var page = await browser.GetStringAsync(new Uri("/my-home", UriKind.Relative), TestContext.Current.CancellationToken);

        var forms = HtmlForm.Parse(page).QuerySelectorAll("form[data-testid^=pay-deposit-]").Select(f => f.GetAttribute("data-testid")).ToList();
        Assert.Equal([$"pay-deposit-{Held}"], forms); // the confirmed reservation has no button

        using var form = new FormUrlEncodedContent(HtmlForm.Fields(page, $"pay-deposit-{Held}", new Dictionary<string, string>()));
        using var response = await browser.PostAsync(new Uri("/my-home", UriKind.Relative), form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_x", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Returning_from_checkout_says_payment_is_awaited_and_marks_nothing_paid()
    {
        var api = new FakeCatalogueApi();
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)), "buyer");

        var html = await browser.GetStringAsync(new Uri($"/my-home?deposit=paid&reservation={Held}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotNull(HtmlForm.Parse(html).QuerySelector("[data-testid=deposit-returned]"));
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Get, r.Method)); // the success URL changes nothing
    }
    // C8 Lab 5: a late payment that was refunded says why, on the reservation it belongs to.
    [Fact]
    public async Task Refunded_late_payment_is_explained_in_my_home()
    {
        var expired = Guid.Parse("0198a000-0000-7000-a000-000000000003");
        var api = new FakeCatalogueApi
        {
            Respond = r => r.RequestUri!.AbsolutePath switch
            {
                "/v2/me/progress" => FakeCatalogueApi.Json(new HomeBuild.Web.Api.MyProgress(
                    [new(expired, Guid.NewGuid(), "P1", "Riverside", "Expired", DateTimeOffset.UtcNow.AddDays(-1), new("Frame", 1, 3, "Roof"))])),
                "/v2/me/deposits" => FakeCatalogueApi.Json(new[]
                {
                    new HomeBuild.Web.Api.MyDeposit(expired, 5000m, "GBP", DateTimeOffset.UtcNow, "refunded",
                        "The hold ended before your payment arrived, and another buyer has since reserved this home. Your deposit has been refunded in full.", DateTimeOffset.UtcNow),
                }),
                _ => null,
            },
        };
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)), "buyer");

        var html = await browser.GetStringAsync(new Uri("/my-home", UriKind.Relative), TestContext.Current.CancellationToken);

        var note = HtmlForm.Parse(html).QuerySelector($"[data-testid=refund-{expired}]")!.TextContent;
        Assert.Contains("Deposit refunded", note, StringComparison.Ordinal);
        Assert.Contains("£5,000.00", note, StringComparison.Ordinal);
        Assert.Contains("another buyer has since reserved this home", note, StringComparison.Ordinal);
    }
}
