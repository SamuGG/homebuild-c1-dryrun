using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>
/// C6 Lab 5: the three journeys that matter, in a real browser against the local stack and the real Cognito pool.
/// Opt-in (HOMEBUILD_COGNITO_TESTS=1): CI has no Cognito sign-in. Assertions use Playwright's auto-waiting
/// Expect(...), never fixed delays.
/// </summary>
[Collection(Journeys.Name)]
public sealed class JourneyTests(Journeys browser)
{
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";
    private const string PlotTwo = "0198a000-0000-7000-8001-000000000002";

    [Fact]
    public async Task Visitor_browses_the_catalogue_and_opens_a_unit()
    {
        Assert.SkipUnless(RealSignInTests.Enabled, "Set HOMEBUILD_COGNITO_TESTS=1 to run the journeys against the local stack.");
        await using var context = await browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(RealSignInTests.WebUrl + "/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Developments", Level = 1 })).ToBeVisibleAsync();
        var keyboard = await Keyboard.CheckTabOrderAsync(page);
        await page.GetByRole(AriaRole.Link, new() { Name = "Riverside" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/developments/{Riverside}$"));
        await page.GetByRole(AriaRole.Link, new() { Name = "Plot P1" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Plot P1", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("unit")).ToContainTextAsync("£");
        Assert.True(keyboard.Problems.Count == 0, string.Join("; ", keyboard.Problems));
        Assert.Equal(keyboard.Focusable, keyboard.Reached);
    }

    [Fact]
    public async Task Buyer_reserves_and_sees_the_reservation_in_my_home()
    {
        Assert.SkipUnless(RealSignInTests.Enabled, "Set HOMEBUILD_COGNITO_TESTS=1 to run the journeys against the local stack.");
        await LocalData.MakeAvailableAsync(PlotTwo);
        var state = await SignedInState.ForAsync(browser.Browser, "buyer.tenant-a@example.com");
        await using var context = await browser.Browser.NewContextAsync(new() { StorageStatePath = state });
        var page = await context.NewPageAsync();

        await page.GotoAsync(RealSignInTests.WebUrl + "/units/" + PlotTwo);
        await Assertions.Expect(page.GetByTestId("reserve-form")).ToBeVisibleAsync(); // signed in from stored state
        var keyboard = await Keyboard.CheckTabOrderAsync(page);
        await page.Locator("#deposit").FillAsync("5000");
        await page.Locator("#terms").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Reserve" }).ClickAsync();

        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/my-home/reservations/[0-9a-f-]{36}$"));
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your reservation" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "My home" }).First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("homes")).ToContainTextAsync("Plot P2, Riverside");
        Assert.True(keyboard.Problems.Count == 0, string.Join("; ", keyboard.Problems));
    }

    [Fact]
    public async Task Staff_filter_the_milestone_board()
    {
        Assert.SkipUnless(RealSignInTests.Enabled, "Set HOMEBUILD_COGNITO_TESTS=1 to run the journeys against the local stack.");
        var state = await SignedInState.ForAsync(browser.Browser, "site-manager.tenant-a@example.com");
        await using var context = await browser.Browser.NewContextAsync(new() { StorageStatePath = state });
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{RealSignInTests.WebUrl}/staff/developments/{Riverside}/board");
        var count = page.GetByTestId("row-count");
        await Assertions.Expect(count).ToHaveTextAsync("3 of 3 plots");
        await Assertions.Expect(page.Locator("#stage option")).ToHaveCountAsync(9); // all stages, 7 milestones, finished
        await Assertions.Expect(page.GetByTestId("board-root")).ToHaveAttributeAsync("data-interactive", "true"); // circuit up
        var keyboard = await Keyboard.CheckTabOrderAsync(page);
        await page.Locator("#plot-search").FillAsync("P1");

        await Assertions.Expect(count).ToHaveTextAsync("1 of 3 plots");
        await page.Locator("#plot-search").FillAsync("");
        var stage = (await page.Locator("#stage option").AllInnerTextsAsync())[1];
        await page.Locator("#stage").SelectOptionAsync(stage);
        await Assertions.Expect(count).ToHaveTextAsync(new Regex(@"^\d of 3 plots$"));
        Assert.True(keyboard.Problems.Count == 0, string.Join("; ", keyboard.Problems));
    }

    [Fact]
    public async Task Escape_closes_the_dialog_and_focus_returns()
    {
        Assert.SkipUnless(RealSignInTests.Enabled, "Set HOMEBUILD_COGNITO_TESTS=1 to run the journeys against the local stack.");
        await using var context = await browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(RealSignInTests.WebUrl + "/dev/components"); // the only page with a dialog (C5 Lab 3)
        var open = page.GetByRole(AriaRole.Button, new() { Name = "Open dialog" });
        await Assertions.Expect(page.GetByTestId("gallery-root")).ToHaveAttributeAsync("data-interactive", "true"); // circuit up

        await open.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var dialog = page.Locator("dialog:not(#components-reconnect-modal)");
        await Assertions.Expect(dialog).ToHaveJSPropertyAsync("open", true);
        await page.Keyboard.PressAsync("Escape");

        await Assertions.Expect(dialog).ToHaveJSPropertyAsync("open", false);
        await Assertions.Expect(open).ToBeFocusedAsync();
    }
}

/// <summary>One browser for the journeys (the collection), launched once.</summary>
[CollectionDefinition(Name)]
public sealed class Journeys : IAsyncLifetime, ICollectionFixture<Journeys>
{
    public const string Name = "Journeys";

    private IPlaywright? playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        playwright = await Playwright.CreateAsync();
        Browser = await playwright.Chromium.LaunchAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Browser.DisposeAsync();
        playwright?.Dispose();
    }
}

/// <summary>Resets the one row the buyer journey changes, in the local compose database (the journey's own data).</summary>
internal static class LocalData
{
    public static async Task MakeAvailableAsync(string unitId)
    {
        var sql = $"DELETE FROM sales.reservations WHERE unit_id = '{unitId}'; UPDATE sales.units SET status = 'Available' WHERE id = '{unitId}';";
        using var psql = Process.Start(new ProcessStartInfo("docker", ["compose", "exec", "-T", "postgres", "psql", "-q", "-v", "ON_ERROR_STOP=1", "-U", "homebuild", "-d", "homebuild", "-c", sql])
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardError = true,
        })!;
        var error = await psql.StandardError.ReadToEndAsync();
        await psql.WaitForExitAsync();
        Assert.True(psql.ExitCode == 0, "Could not reset the unit: " + error);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "HomeBuild.slnx")))
        {
            dir = dir.Parent;
        }

        return dir.FullName;
    }
}
