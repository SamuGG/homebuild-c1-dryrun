using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>
/// C10 Lab 3: My Home never shows a state before the system reaches it. Opt-in (HOMEBUILD_COGNITO_TESTS=1 and
/// HOMEBUILD_AWS_TESTS=1): the local web app and API (Debug, Development, with Messaging:TopicArn and
/// ChangeOrders:QueueUrl set), the real Cognito pool, SNS and homebuild-change-orders.
/// </summary>
[Collection(Journeys.Name)]
public sealed class ChangeOrderJourneyTests(Journeys browser)
{
    private const string PlotThree = "0198a000-0000-7000-8001-000000000003";
    private static readonly bool AwsEnabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";
    private static readonly string ApiUrl = Environment.GetEnvironmentVariable("HOMEBUILD_API_URL") ?? "http://localhost:5104";

    [Fact]
    public async Task My_home_shows_pending_until_event_processed()
    {
        Assert.SkipUnless(RealSignInTests.Enabled && AwsEnabled, "Set HOMEBUILD_COGNITO_TESTS=1 and HOMEBUILD_AWS_TESTS=1 to run against the local stack and AWS.");
        var log = TestContext.Current.TestOutputHelper;
        var clock = Stopwatch.StartNew();
        using var http = new HttpClient();
        await ConsumerAsync(http, "pause"); // the change-order worker stops receiving: ExtrasPaid will wait in its queue

        // The buyer holds plot 3 (reserved through the UI, as in C6's journey).
        await LocalData.MakeAvailableAsync(PlotThree);
        var state = await SignedInState.ForAsync(browser.Browser, "buyer.tenant-a@example.com");
        await using var context = await browser.Browser.NewContextAsync(new() { StorageStatePath = state });
        var page = await context.NewPageAsync();
        await page.GotoAsync(RealSignInTests.WebUrl + "/units/" + PlotThree);
        await page.Locator("#deposit").FillAsync("5000");
        await page.Locator("#terms").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Reserve" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/my-home/reservations/[0-9a-f-]{36}$"));

        // The payment webhook's outcome, as Lab 2 records it: a paid basket and ExtrasPaid in Extras' outbox. The API's
        // relay publishes it to SNS; homebuild-change-orders holds it while the worker is paused.
        var line = await ExtrasData.PaidKitchenAsync(PlotThree);
        log?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s paid basket line {line}; worker paused");

        await page.GotoAsync(RealSignInTests.WebUrl + "/my-home");
        var lineState = page.GetByTestId("line-" + line).GetByTestId("state");
        await Assertions.Expect(lineState).ToHaveTextAsync("Paid");
        await page.WaitForTimeoutAsync(10_000); // longer than relay → SNS → SQS takes; the event is in the queue now
        await page.ReloadAsync();
        await Assertions.Expect(lineState).ToHaveTextAsync("Paid"); // still: no change order exists, so nothing more is said
        log?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s still \"Paid\" with the worker paused");

        await ConsumerAsync(http, "resume");
        var until = DateTime.UtcNow.AddMinutes(1);
        while (await lineState.TextContentAsync() != "Ordered on site")
        {
            Assert.True(DateTime.UtcNow < until, "\"Ordered on site\" did not appear within a minute of resuming the worker");
            await page.WaitForTimeoutAsync(1_000);
            await page.ReloadAsync(); // My Home refreshes on navigation (static SSR)
        }

        log?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s \"Ordered on site\" after the worker resumed");
    }

    private static async Task ConsumerAsync(HttpClient http, string action)
    {
        using var response = await http.PostAsync(new Uri($"{ApiUrl}/dev/consumers/change-orders/{action}"), null);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {response.StatusCode} (a Debug build of the API in Development?)");
    }
}

/// <summary>Local database set-up for the change-order journey (docker compose's PostgreSQL, the owner role).</summary>
internal static class ExtrasData
{
    private static readonly Guid Tenant = Guid.Parse("0198a000-0000-7000-8000-00000000000a");

    /// <summary>A paid basket with the Premium kitchen for the unit's current buyer, and its ExtrasPaid in Extras' outbox; returns the line ID.</summary>
    public static async Task<Guid> PaidKitchenAsync(string unitId)
    {
        var basket = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        var buyer = Guid.Parse(await PsqlAsync($"SELECT buyer_id FROM sales.reservations WHERE unit_id = '{unitId}' AND status = 'held' ORDER BY expires_at DESC LIMIT 1;"));
        var extra = Guid.Parse(await PsqlAsync("SELECT id FROM extras.extras WHERE name = 'Premium kitchen' AND development_id = '0198a000-0000-7000-8000-000000000001';"));
        // ExtrasPaidMessage's JSON (the web tests do not reference the domain project).
        var payload = JsonSerializer.Serialize(new
        {
            basketId = basket, unitId = Guid.Parse(unitId), tenantId = Tenant, buyerId = buyer,
            extras = new[] { new { lineId = line, extraId = extra, name = "Premium kitchen", unitPrice = 6500m, currency = "GBP", quantity = 1, fitBy = "FirstFix" } },
            total = 6500m, currency = "GBP", paidAt = DateTimeOffset.UtcNow,
        });
        await PsqlAsync($"""
            INSERT INTO extras.baskets (id, buyer_id, unit_id, development_id, status, created_at, checkout_attempts, session_id, payment_intent, charged_amount, paid_at, tenant_id)
              VALUES ('{basket}', '{buyer}', '{unitId}', '0198a000-0000-7000-8000-000000000001', 'Paid', now(), 1, 'cs_e2e_{basket:N}', 'pi_e2e', 6500, now(), '{Tenant}');
            INSERT INTO extras.basket_lines (id, basket_id, extra_id, name, unit_price, currency, quantity, added_at, status, tenant_id)
              VALUES ('{line}', '{basket}', '{extra}', 'Premium kitchen', 6500, 'GBP', 1, now(), 'Paid', '{Tenant}');
            INSERT INTO extras.outbox_messages (id, type, payload, tenant_id, occurred_at, attempts)
              VALUES ('{Guid.CreateVersion7()}', 'ExtrasPaid', '{payload.Replace("'", "''", StringComparison.Ordinal)}', '{Tenant}', now(), 0);
            """);
        return line;
    }

    private static async Task<string> PsqlAsync(string sql)
    {
        using var psql = Process.Start(new ProcessStartInfo(Path.Combine(RepoRoot(), "scripts", "local-psql.sh"), ["-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-c", sql])
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = await psql.StandardOutput.ReadToEndAsync();
        var error = await psql.StandardError.ReadToEndAsync();
        await psql.WaitForExitAsync();
        Assert.True(psql.ExitCode == 0, "psql failed: " + error);
        return output.Trim();
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
