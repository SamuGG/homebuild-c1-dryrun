using System.Net;
using System.Text.Json;
using HomeBuild.Web.Auth;

namespace HomeBuild.Web.Tests;

/// <summary>C5 Lab 7, web side: the two relay endpoints around a direct-to-S3 upload.</summary>
public sealed class FloorPlanRelayTests
{
    private static readonly string EditPage = $"/staff/units/{FakeCatalogueApi.PlotOne}/edit";
    private static readonly string Ticket = $"/staff/units/{FakeCatalogueApi.PlotOne}/floor-plan/ticket";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_gets_the_apis_presigned_post_with_the_antiforgery_header_only()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var admin = factory.CreateBrowser();
        await factory.SignInAsync(admin, new TokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1)), "admin");
        var page = HtmlForm.Parse(await admin.GetStringAsync(new Uri(EditPage, UriKind.Relative), Ct));
        var token = page.QuerySelector("floor-plan-upload input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var without = await admin.PostAsync(new Uri(Ticket, UriKind.Relative), new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, Ticket) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("RequestVerificationToken", token);
        using var with = await admin.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        var ticket = JsonElement.Parse(await with.Content.ReadAsStringAsync(Ct));
        Assert.Equal("incoming/t/u/f.pdf", ticket.GetProperty("key").GetString());
        Assert.Equal("application/pdf", ticket.GetProperty("fields").GetProperty("Content-Type").GetString());
    }

    [Fact]
    public async Task Non_admins_cannot_start_an_upload()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var buyer = factory.CreateBrowser();
        await factory.SignInAsync(buyer, new TokenSet("a", "r", DateTimeOffset.UtcNow.AddHours(1)), "buyer");

        using var response = await buyer.PostAsync(new Uri(Ticket, UriKind.Relative), new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("/access-denied", response.Headers.Location?.PathAndQuery ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Web_app_has_no_aws_sdk()
    {
        var references = typeof(HomeBuild.Web.Components.App).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(references, name => name.StartsWith("AWSSDK", StringComparison.Ordinal) || name.StartsWith("Amazon", StringComparison.Ordinal));
    }
}
