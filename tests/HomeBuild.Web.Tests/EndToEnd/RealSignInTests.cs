using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>
/// Signs in through the real Cognito managed login with Playwright, against the web app and API running locally
/// (see docs/identity.md). Opt-in: HOMEBUILD_COGNITO_TESTS=1. CI cannot sign in to Cognito and never runs these.
/// The test user's password is read from the local password file (HOMEBUILD_TEST_USERS) and never printed.
/// </summary>
public sealed partial class RealSignInTests
{
    private const string User = "buyer.tenant-a@example.com";

    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_COGNITO_TESTS") == "1";

    private static readonly string WebUrl = Environment.GetEnvironmentVariable("HOMEBUILD_WEB_URL") ?? "http://localhost:5266";

    [Fact]
    public async Task Browser_storage_has_no_tokens()
    {
        Assert.SkipUnless(Enabled, "Set HOMEBUILD_COGNITO_TESTS=1 to sign in to the real user pool.");
        await using var session = await SignedInPageAsync();
        var page = session.Page;

        // Everything a script on the page can read.
        var readable = await page.EvaluateAsync<string[]>("""
            () => [
              ...Object.keys(localStorage).map(k => `localStorage ${k}=${localStorage.getItem(k)}`),
              ...Object.keys(sessionStorage).map(k => `sessionStorage ${k}=${sessionStorage.getItem(k)}`),
              `document.cookie=${document.cookie}`,
              `html=${document.documentElement.outerHTML}`,
            ]
            """);
        Assert.All(readable, entry => Assert.DoesNotMatch(Jwt(), entry));

        var cookies = await page.Context.CookiesAsync([WebUrl]);
        var sessionCookie = Assert.Single(cookies, c => c.Name.StartsWith("homebuild.session", StringComparison.Ordinal));
        Assert.True(sessionCookie.HttpOnly);
        Assert.True(sessionCookie.Secure);
        Assert.Equal(SameSiteAttribute.Lax, sessionCookie.SameSite);
        Assert.DoesNotMatch(Jwt(), sessionCookie.Value); // encrypted principal and session ID only
        Assert.All(cookies.Where(c => !c.HttpOnly), c => Assert.DoesNotMatch(Jwt(), c.Value));
    }

    [Fact]
    public async Task Sign_out_ends_the_cognito_session_too()
    {
        Assert.SkipUnless(Enabled, "Set HOMEBUILD_COGNITO_TESTS=1 to sign in to the real user pool.");
        await using var session = await SignedInPageAsync();
        var page = session.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await page.WaitForURLAsync(WebUrl + "/");
        await page.GotoAsync(WebUrl + "/my-home");

        // Not signed back in silently: Cognito asks for the password again (a shared computer).
        await page.WaitForURLAsync(new Regex(@"amazoncognito\.com/login"));
        Assert.True(await page.Locator("input[type=password]").IsVisibleAsync());
    }

    private static async Task<BrowserSession> SignedInPageAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(WebUrl + "/my-home");
        await page.WaitForURLAsync(new Regex(@"amazoncognito\.com/login"));
        await page.Locator("input[type=email]").FillAsync(User);
        await page.Locator("input[type=password]").FillAsync(Password(User));
        await page.Locator("button[type=submit]").ClickAsync();
        await page.WaitForURLAsync(WebUrl + "/my-home");
        Assert.Contains(User, await page.Locator("[data-testid=me]").InnerTextAsync(), StringComparison.Ordinal);
        return new BrowserSession(playwright, browser, page);
    }

    // dryrun/test-users.local: email, tenant, sub, password (tab-separated), outside the repository.
    private static string Password(string email)
    {
        var path = Environment.GetEnvironmentVariable("HOMEBUILD_TEST_USERS")
            ?? throw new InvalidOperationException("Set HOMEBUILD_TEST_USERS to the test users' password file.");
        return File.ReadLines(path).Select(l => l.Split('\t')).Single(f => f[0] == email)[^1];
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.")]
    private static partial Regex Jwt();

    private sealed record BrowserSession(IPlaywright Playwright, IBrowser Browser, IPage Page) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Browser.DisposeAsync();
            Playwright.Dispose();
        }
    }
}
