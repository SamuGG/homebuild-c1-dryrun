using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>
/// Signs each test user in through Cognito's managed login once, and saves the browser's storage state (the session
/// cookie) to a file outside the repository. Journeys start from that state (StorageStatePath), so they do not sign in
/// through the UI each time. The state is reused across test runs while the web app (which holds the server-side tokens)
/// keeps running; a state that no longer works is made again.
/// </summary>
internal static class SignedInState
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "homebuild-e2e-state");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string> ForAsync(IBrowser browser, string user)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, user + ".json");
        await Gate.WaitAsync();
        try
        {
            if (File.Exists(path) && await StillSignedInAsync(browser, path))
            {
                return path;
            }

            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync(RealSignInTests.WebUrl + "/my-home");
            await page.WaitForURLAsync(new Regex(@"amazoncognito\.com/login"));
            await page.Locator("input[type=email]").FillAsync(user);
            await page.Locator("input[type=password]").FillAsync(RealSignInTests.Password(user));
            await page.Locator("button[type=submit]").ClickAsync();
            await page.WaitForURLAsync(RealSignInTests.WebUrl + "/my-home");
            await context.StorageStateAsync(new() { Path = path });
            SignIns++;
            return path;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>How many UI sign-ins this test run needed (0 when every state file was reused).</summary>
    public static int SignIns { get; private set; }

    private static async Task<bool> StillSignedInAsync(IBrowser browser, string path)
    {
        await using var context = await browser.NewContextAsync(new() { StorageStatePath = path });
        var page = await context.NewPageAsync();
        var response = await page.GotoAsync(RealSignInTests.WebUrl + "/my-home");
        // C11 Lab 5: staying on localhost is not enough. When the web app no longer has the session's tokens (a restart, a
        // new token store), it ends the session, and Cognito's own session cookie (also in the saved state) signs the
        // browser straight back in: a new web session that exists only in this context. So the state counts only if
        // the page shows "Sign out", and it is saved again, so the caller gets the session that works now.
        if (response is null || new Uri(response.Url).Host != "localhost"
            || await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign out" }).CountAsync() == 0)
        {
            return false;
        }

        await context.StorageStateAsync(new() { Path = path });
        return true;
    }
}
