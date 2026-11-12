using System.Net;
using HomeBuild.Web.Auth;

namespace HomeBuild.Web.Tests;

/// <summary>C5 Lab 5: the reservation form (buyers) and the staff unit edit form (admins), against a fake API.</summary>
public sealed class FormsTests
{
    private static readonly Guid PlotOne = FakeCatalogueApi.PlotOne;
    private static readonly string UnitPage = $"/units/{PlotOne}";
    private static readonly string ReservationsPath = $"/v2/units/{PlotOne}/reservations";

    private static TokenSet Fresh => new("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> DepositProblems => ["400", "422"];

    [Theory]
    [MemberData(nameof(DepositProblems))]
    public async Task Invalid_deposit_shows_field_error_from_api(string status)
    {
        var api = new FakeCatalogueApi
        {
            Respond = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == ReservationsPath
                ? status == "400"
                    ? FakeCatalogueApi.Problem(HttpStatusCode.BadRequest, "One or more validation errors occurred.",
                        errors: new() { ["Deposit"] = ["The field Deposit must be between 0.01 and 100000000."] })
                    : FakeCatalogueApi.Problem(HttpStatusCode.UnprocessableEntity, "Deposit rejected",
                        "Deposit 50 GBP is outside the allowed range (minimum 1000 GBP).")
                : null,
        };
        var html = await PostReservationAsync(api, deposit: "50");

        // The API's message sits next to the deposit input, not only in a summary.
        var message = Assert.Single(HtmlForm.MessagesFor(html, "deposit"));
        Assert.Contains(status == "400" ? "between 0.01 and 100000000" : "minimum 1000 GBP", message, StringComparison.Ordinal);
        Assert.Equal("50", HtmlForm.Parse(html).QuerySelector("#deposit")!.GetAttribute("value")); // what the buyer typed stays
    }

    [Fact]
    public async Task Unit_already_reserved_shows_conflict_message()
    {
        var api = new FakeCatalogueApi
        {
            Respond = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == ReservationsPath
                ? FakeCatalogueApi.Problem(HttpStatusCode.Conflict, "Unit unavailable", $"Unit {PlotOne} is Reserved.")
                : null,
        };

        var html = await PostReservationAsync(api, deposit: "5000");

        Assert.Equal([$"Unit unavailable: Unit {PlotOne} is Reserved."], HtmlForm.Summary(html));
        Assert.Empty(HtmlForm.MessagesFor(html, "deposit"));
    }

    [Fact]
    public async Task Form_post_without_antiforgery_token_is_rejected()
    {
        var api = new FakeCatalogueApi();
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, Fresh, "buyer");
        var page = await browser.GetStringAsync(new Uri(UnitPage, UriKind.Relative), Ct);

        using var form = new FormUrlEncodedContent(HtmlForm.Fields(page, "reserve-form",
            new Dictionary<string, string> { ["deposit"] = "5000", ["terms"] = "true" }, withAntiforgery: false));
        using var response = await browser.PostAsync(new Uri(UnitPage, UriKind.Relative), form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(api.Bodies, b => b.StartsWith("POST", StringComparison.Ordinal)); // the API was never called
    }

    [Fact]
    public async Task Successful_reservation_redirects_to_my_home()
    {
        var reservationId = Guid.CreateVersion7();
        var reservation = new HomeBuild.Web.Api.Reservation(reservationId, PlotOne, Guid.NewGuid(), 5000m, "GBP", "Pending", DateTimeOffset.UtcNow.AddDays(7));
        var api = new FakeCatalogueApi
        {
            Respond = r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
            {
                ("POST", var p) when p == ReservationsPath => FakeCatalogueApi.Json(reservation, HttpStatusCode.Created),
                ("GET", var p) when p == $"/v2/reservations/{reservationId}" => FakeCatalogueApi.Json(reservation),
                _ => null,
            },
        };
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, Fresh, "buyer");
        var page = await browser.GetStringAsync(new Uri(UnitPage, UriKind.Relative), Ct);

        using var form = new FormUrlEncodedContent(HtmlForm.Fields(page, "reserve-form",
            new Dictionary<string, string> { ["deposit"] = "5000", ["terms"] = "true" }));
        using var post = await browser.PostAsync(new Uri(UnitPage, UriKind.Relative), form, Ct);

        // Post-redirect-get to the reservation in My Home.
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal($"/my-home/reservations/{reservationId}", post.Headers.Location!.AbsolutePath);
        Assert.Contains($"POST {ReservationsPath} {{\"deposit\":5000,\"currency\":\"GBP\"}}", api.Bodies);
        var landed = await browser.GetStringAsync(post.Headers.Location, Ct);
        Assert.Contains("Your reservation", landed, StringComparison.Ordinal);
        Assert.Contains("&#xA3;5,000", landed, StringComparison.Ordinal); // Razor encodes £
    }

    [Fact]
    public async Task Enhanced_form_post_gets_a_redirect_that_fetch_follows()
    {
        var reservationId = Guid.CreateVersion7();
        var api = new FakeCatalogueApi
        {
            Respond = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == ReservationsPath
                ? FakeCatalogueApi.Json(new HomeBuild.Web.Api.Reservation(reservationId, PlotOne, Guid.NewGuid(), 5000m, "GBP", "Pending", DateTimeOffset.UtcNow), HttpStatusCode.Created)
                : null,
        };
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, Fresh, "buyer");
        var page = await browser.GetStringAsync(new Uri(UnitPage, UriKind.Relative), Ct);
        Assert.NotNull(HtmlForm.Parse(page).QuerySelector("form[data-testid=reserve-form][data-enhance]"));

        // What blazor.web.js sends for an enhanced form: a fetch with this Accept header.
        using var request = new HttpRequestMessage(HttpMethod.Post, UnitPage)
        {
            Content = new FormUrlEncodedContent(HtmlForm.Fields(page, "reserve-form", new Dictionary<string, string> { ["deposit"] = "5000", ["terms"] = "true" })),
        };
        request.Headers.Accept.ParseAdd("text/html; blazor-enhanced-nav=on");
        using var response = await browser.SendAsync(request, Ct);

        // With BlazorDisableThrowNavigationException (the .NET 11 template default) NavigateTo answers an enhanced post
        // with a plain 302; blazor.web.js's fetch follows it and patches the page with My Home, so no full reload.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/my-home/reservations/{reservationId}", response.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task Terms_not_accepted_is_caught_in_the_form_before_the_api()
    {
        var api = new FakeCatalogueApi();
        var html = await PostReservationAsync(api, deposit: "5000", terms: null);

        Assert.Contains("Accept the reservation terms to reserve.", HtmlForm.Summary(html));
        Assert.DoesNotContain(api.Bodies, b => b.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_edits_price_and_description_and_sees_api_field_errors()
    {
        var api = new FakeCatalogueApi
        {
            Respond = r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == $"/v2/units/{PlotOne}"
                ? r.Content!.ReadAsStringAsync().Result.Contains("\"price\":999", StringComparison.Ordinal)
                    ? FakeCatalogueApi.Problem(HttpStatusCode.BadRequest, "One or more validation errors occurred.", errors: new() { ["price"] = ["Price is below the floor."] })
                    : new HttpResponseMessage(HttpStatusCode.NoContent)
                : null,
        };
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, Fresh, "admin");
        var editPage = $"/staff/units/{PlotOne}/edit";
        var page = await browser.GetStringAsync(new Uri(editPage, UriKind.Relative), Ct);
        Assert.Equal("260000", HtmlForm.Parse(page).QuerySelector("#price")!.GetAttribute("value"));

        using var bad = new FormUrlEncodedContent(HtmlForm.Fields(page, "edit-unit-form", new Dictionary<string, string> { ["price"] = "999", ["description"] = "x" }));
        var badHtml = await (await browser.PostAsync(new Uri(editPage, UriKind.Relative), bad, Ct)).Content.ReadAsStringAsync(Ct);
        using var good = new FormUrlEncodedContent(HtmlForm.Fields(page, "edit-unit-form", new Dictionary<string, string> { ["price"] = "265000", ["description"] = "Corner plot" }));
        using var saved = await browser.PostAsync(new Uri(editPage, UriKind.Relative), good, Ct);

        Assert.Equal(["Price is below the floor."], HtmlForm.MessagesFor(badHtml, "price")); // camelCase key, PascalCase field
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal($"{editPage}?saved=true", saved.Headers.Location!.PathAndQuery);
        Assert.Contains($"PUT /v2/units/{PlotOne} {{\"price\":265000,\"description\":\"Corner plot\"}}", api.Bodies);
    }

    [Fact]
    public async Task Only_admins_can_open_the_unit_edit_form_and_only_buyers_see_the_reservation_form()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var buyer = factory.CreateBrowser();
        await factory.SignInAsync(buyer, Fresh, "buyer");
        using var admin = factory.CreateBrowser();
        await factory.SignInAsync(admin, Fresh, "admin");

        using var forbidden = await buyer.GetAsync(new Uri($"/staff/units/{PlotOne}/edit", UriKind.Relative), Ct);
        var buyerUnit = await buyer.GetStringAsync(new Uri(UnitPage, UriKind.Relative), Ct);
        var adminUnit = await admin.GetStringAsync(new Uri(UnitPage, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, forbidden.StatusCode);
        Assert.StartsWith("/access-denied", forbidden.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"reserve-form\"", buyerUnit, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"reserve-form\"", adminUnit, StringComparison.Ordinal);
        Assert.Contains("Edit this unit", adminUnit, StringComparison.Ordinal);
    }

    private static async Task<string> PostReservationAsync(FakeCatalogueApi api, string deposit, string? terms = "true")
    {
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, Fresh, "buyer");
        var page = await browser.GetStringAsync(new Uri(UnitPage, UriKind.Relative), Ct);
        var values = new Dictionary<string, string> { ["deposit"] = deposit };
        if (terms is not null)
        {
            values["terms"] = terms;
        }

        using var form = new FormUrlEncodedContent(HtmlForm.Fields(page, "reserve-form", values));
        using var response = await browser.PostAsync(new Uri(UnitPage, UriKind.Relative), form, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }
}
