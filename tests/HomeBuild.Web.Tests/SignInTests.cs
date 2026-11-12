using System.Net;
using System.Text.RegularExpressions;
using HomeBuild.Web.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Web.Tests;

public sealed partial class SignInTests
{
    private static TokenSet Fresh => new("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1));

    private static TokenSet Expired => new("expired-access", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-5));

    [Fact]
    public async Task Signed_out_user_is_redirected_to_login_for_my_home()
    {
        var api = new FakeCatalogueApi();
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        var ct = TestContext.Current.CancellationToken;

        // Never signed in: challenged to Cognito, code flow with PKCE.
        using (var anonymous = factory.CreateBrowser())
        {
            using var challenge = await anonymous.GetAsync(new Uri("/my-home", UriKind.Relative), ct);
            Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
            var location = challenge.Headers.Location!.ToString();
            Assert.StartsWith(FakeCognito.Domain + "/oauth2/authorize?client_id=web-test", location, StringComparison.Ordinal);
            Assert.Contains("code_challenge_method=S256", location, StringComparison.Ordinal);
            Assert.Contains("response_type=code", location, StringComparison.Ordinal);
        }

        // Signed in: My home renders, with the API called using the server-side token.
        var sessionId = await factory.SignInAsync(browser, Fresh);
        var page = await browser.GetStringAsync(new Uri("/my-home", UriKind.Relative), ct);
        Assert.Contains("buyer.tenant-a@example.com", page, StringComparison.Ordinal);
        Assert.Equal("Bearer access-1", api.Requests.Single(r => r.RequestUri!.AbsolutePath == "/v2/me").Headers.Authorization!.ToString());

        // Sign out (antiforgery-protected POST): the tokens are gone and the browser goes to Cognito's logout.
        var antiforgery = AntiforgeryField().Match(page).Groups["token"].Value;
        Assert.NotEmpty(antiforgery);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = antiforgery });
        using var signOut = await browser.PostAsync(new Uri("/account/logout", UriKind.Relative), form, ct);
        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.Equal(FakeCognito.Domain + "/logout?client_id=web-test&logout_uri=https%3A%2F%2Flocalhost%2F", signOut.Headers.Location!.ToString());
        Assert.Contains(signOut.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SignIn.CookieName + "=;", StringComparison.Ordinal));
        Assert.Null(await factory.Services.GetRequiredService<TokenStore>().GetAsync(sessionId, ct));

        // The old cookie (a shared computer, or a copy) no longer opens My home: back to the login.
        using var after = await browser.GetAsync(new Uri("/my-home", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith(FakeCognito.Domain + "/oauth2/authorize", after.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_out_without_antiforgery_token_is_rejected()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var browser = factory.CreateBrowser();
        var sessionId = await factory.SignInAsync(browser, Fresh);
        var ct = TestContext.Current.CancellationToken;

        // A same-origin form with every field but the token: the token check rejects it.
        using var noToken = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "/" });
        using var signOut = await browser.PostAsync(new Uri("/account/logout", UriKind.Relative), noToken, ct);

        Assert.Equal(HttpStatusCode.BadRequest, signOut.StatusCode);
        Assert.NotNull(await factory.Services.GetRequiredService<TokenStore>().GetAsync(sessionId, ct));
    }

    [Fact]
    public async Task Cross_site_sign_out_is_rejected()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var browser = factory.CreateBrowser();
        var sessionId = await factory.SignInAsync(browser, Fresh);
        var ct = TestContext.Current.CancellationToken;

        // What a browser sends when another site's page posts to HomeBuild with the user's cookie. The attacker's
        // page cannot read HomeBuild's antiforgery token, so there is none. (With UseAntiforgery, a valid token
        // would override the Sec-Fetch-Site verdict: "token validation takes precedence" in .NET 11.)
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/logout")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["returnUrl"] = "/" }),
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", "https://attacker.test");
        using var signOut = await browser.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadRequest, signOut.StatusCode);
        Assert.NotNull(await factory.Services.GetRequiredService<TokenStore>().GetAsync(sessionId, ct));
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_once()
    {
        var api = new FakeCatalogueApi();
        await using var factory = new WebFactory(api);
        using var browser = factory.CreateBrowser();
        var ct = TestContext.Current.CancellationToken;
        var sessionId = await factory.SignInAsync(browser, Expired);

        // Two page loads, and five parallel token requests for the same session in between.
        await browser.GetStringAsync(new Uri("/my-home", UriKind.Relative), ct);
        var tokens = factory.Services.GetRequiredService<UserTokenService>();
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(TokenStore.SessionClaim, sessionId)], "test"));
        await factory.Services.GetRequiredService<TokenStore>().SetAsync(sessionId, Expired with { AccessToken = "expired-again" }, ct);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => tokens.GetAccessTokenAsync(user, ct)));
        await browser.GetStringAsync(new Uri("/my-home", UriKind.Relative), ct);

        // One refresh for the first expiry, one for the second: never one per caller.
        Assert.Equal(2, factory.Cognito.Refreshes);
        Assert.All(parallel, t => Assert.Equal("refreshed-access-2", t));
        Assert.Contains("grant_type=refresh_token", factory.Cognito.LastGrant, StringComparison.Ordinal);
        var meCalls = api.Requests.Where(r => r.RequestUri!.AbsolutePath == "/v2/me").Select(r => r.Headers.Authorization!.Parameter).ToList();
        Assert.Equal(["refreshed-access-1", "refreshed-access-2"], meCalls);
        Assert.Equal("refresh-1", (await factory.Services.GetRequiredService<TokenStore>().GetAsync(sessionId, ct))!.RefreshToken);
    }

    [Fact]
    public async Task Session_cookie_is_HttpOnly_Secure_Lax_and_holds_no_token()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Lax, options.Cookie.SameSite);
        var oidc = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);
        Assert.False(oidc.SaveTokens); // tokens live in the TokenStore, not in the (encrypted) cookie
        Assert.True(oidc.UsePkce);
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="(?<token>[^"]+)" """)]
    private static partial Regex AntiforgeryField();
}
