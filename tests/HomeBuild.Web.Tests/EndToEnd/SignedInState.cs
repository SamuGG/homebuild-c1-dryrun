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
        var response = await (await context.NewPageAsync()).GotoAsync(RealSignInTests.WebUrl + "/my-home");
        return response is not null && new Uri(response.Url).Host == "localhost";
    }
}
