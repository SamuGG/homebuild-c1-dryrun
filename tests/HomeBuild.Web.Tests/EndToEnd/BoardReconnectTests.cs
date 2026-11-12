using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>
/// The board island through a dropped connection, in a real browser against the local stack (opt-in like
/// <see cref="RealSignInTests"/>). The connection is cut from the page with Blazor's own test hook, which closes the
/// SignalR socket the way a network drop does; on localhost, switching Wi-Fi off does not affect the connection.
/// </summary>
public sealed class BoardReconnectTests
{
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";

    [Fact]
    public async Task Board_keeps_its_input_through_a_dropped_connection()
    {
        Assert.SkipUnless(RealSignInTests.Enabled, "Set HOMEBUILD_COGNITO_TESTS=1 to sign in to the real user pool.");
        var path = $"/staff/developments/{Riverside}/board";
        await using var session = await RealSignInTests.SignedInPageAsync("site-manager.tenant-a@example.com", path);
        var page = session.Page;
        await page.WaitForFunctionAsync("() => window.Blazor && document.querySelector('#stage')");
        await page.WaitForTimeoutAsync(1500); // the circuit starts after the prerendered HTML

        // Input in the island: a stage filter and a half-typed plot search.
        await page.Locator("#plot-search").FillAsync("P");
        var stage = (await page.Locator("#stage option").AllInnerTextsAsync())[3]; // a real milestone, from the API
        await page.Locator("#stage").SelectOptionAsync(stage);
        var before = await page.Locator("[data-testid=row-count]").InnerTextAsync();

        // Drop the connection: the reconnect UI must show. Its states are recorded as they happen (it can be quick).
        await page.EvaluateAsync("""
            () => {
              const modal = document.getElementById('components-reconnect-modal');
              window.__reconnect = [];
              new MutationObserver(() => window.__reconnect.push(
                modal.className + (modal.open ? ' open: ' + [...modal.querySelectorAll('p')].filter(p => p.checkVisibility()).map(p => p.textContent.trim()).join(' ') : ''))
              ).observe(modal, { attributes: true });
              Blazor._internal.forceCloseConnection();
            }
            """);
        var modal = page.Locator("#components-reconnect-modal");

        // Back again: same circuit, same input, still interactive.
        await Assertions.Expect(modal).ToBeHiddenAsync(new() { Timeout = 30000 });
        Assert.Equal(stage, await page.Locator("#stage").InputValueAsync());
        Assert.Equal("P", await page.Locator("#plot-search").InputValueAsync());
        Assert.Equal(before, await page.Locator("[data-testid=row-count]").InnerTextAsync());
        await page.Locator("#stage").SelectOptionAsync("");
        await Assertions.Expect(page.Locator("[data-testid=row-count]")).ToHaveTextAsync(new Regex(@"^3 of 3 plots$"));
        var states = await page.EvaluateAsync<string[]>("() => window.__reconnect");
        Assert.Contains(states, s => s.StartsWith("components-reconnect-show open: Rejoining the server", StringComparison.Ordinal));
        Assert.Equal("components-reconnect-hide", states[^1]);
        TestContext.Current.TestOutputHelper?.WriteLine("Reconnect UI states: " + string.Join(" -> ", states));
    }
}
