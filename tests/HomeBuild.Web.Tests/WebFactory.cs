using System.Security.Claims;
using HomeBuild.Web.Api;
using HomeBuild.Web.Auth;
using HomeBuild.Web.Catalogue;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeBuild.Web.Tests;

/// <summary>The web app in memory, with the API replaced by <see cref="FakeCatalogueApi"/> and Cognito by <see cref="FakeCognito"/>.</summary>
internal sealed class WebFactory(FakeCatalogueApi api, FakeCognito? cognito = null) : WebApplicationFactory<Program>
{
    public FakeCognito Cognito { get; } = cognito ?? new FakeCognito();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Authentication:Cognito:Authority", "https://cognito-idp.test/pool");
        builder.UseSetting("Authentication:Cognito:ClientId", "web-test");
        builder.UseSetting("Authentication:Cognito:ClientSecret", "test-secret");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<CatalogueClient>().ConfigurePrimaryHttpMessageHandler(() => api);
            services.AddHttpClient<HomeApiClient>().ConfigurePrimaryHttpMessageHandler(() => api);
            services.AddHttpClient(UserTokenService.TokenClient).ConfigurePrimaryHttpMessageHandler(() => Cognito);
            // A fixed discovery document: no call to a real Cognito.
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o => o.Configuration = FakeCognito.Configuration);
        });
    }

    /// <summary>An HTTPS client (the session cookie is Secure) that does not follow redirects.</summary>
    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    /// <summary>Signs a user in without Cognito: tokens in the server-side store, and the cookie the app would issue.</summary>
    public async Task<string> SignInAsync(HttpClient browser, TokenSet tokens)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        await Services.GetRequiredService<TokenStore>().SetAsync(sessionId, tokens);
        var identity = new ClaimsIdentity(
            [new Claim("sub", "sub-1"), new Claim("email", "buyer.tenant-a@example.com"), new Claim(TokenStore.SessionClaim, sessionId)],
            "test", "email", "cognito:groups");
        var cookies = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var value = cookies.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme));
        browser.DefaultRequestHeaders.Add("Cookie", $"{SignIn.CookieName}={value}");
        return sessionId;
    }
}
