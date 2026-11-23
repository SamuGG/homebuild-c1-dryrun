using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HomeBuild.Web.Tests.EndToEnd;

/// <summary>
/// C10 Lab 7: phases 3 and 4 end to end, opt-in (HOMEBUILD_COGNITO_TESTS=1 and HOMEBUILD_AWS_TESTS=1). Runs against the
/// local web app and a Debug API in Development (relay on; Extras and ChangeOrders queues, Storage and CloudFront
/// configured), the real Cognito pool, SNS, SQS, S3 and CloudFront, Stripe test mode with <c>stripe listen</c>
/// forwarding to the API. Each journey resets Riverside plot 3 first, waits for states (never fixed delays), and
/// leaves the API's test clock as it found it.
/// </summary>
[Collection(Journeys.Name)]
public sealed class PhaseThreeFourJourneyTests(Journeys browser)
{
    internal const string PlotThree = "0198a000-0000-7000-8001-000000000003";
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";
    private static readonly bool AwsEnabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";
    private static readonly string ApiUrl = Environment.GetEnvironmentVariable("HOMEBUILD_API_URL") ?? "http://localhost:5104";

    // Extras unlock after a milestone (Construction's MilestoneCompleted through SNS and homebuild-extras), the buyer pays
    // a basket on Stripe's test Checkout (the webhook through stripe listen), the change-order worker orders it on site,
    // and the site manager acknowledges and fits it: the buyer sees each state only once it is reached.
    [Fact]
    public async Task Buyer_unlocks_pays_and_follows_extras()
    {
        Assert.SkipUnless(RealSignInTests.Enabled && AwsEnabled, "Set HOMEBUILD_COGNITO_TESTS=1 and HOMEBUILD_AWS_TESTS=1 to run against the local stack and AWS.");
        var log = Log.Start();
        await JourneyData.ResetPlotThreeAsync();
        var buyer = await ReservePlotThreeAsync();
        log.Step("plot 3 reserved through the UI");

        await JourneyData.MilestoneCompletedAsync(PlotThree, "Roof"); // what the site manager's completion writes to Construction's outbox
        var kitchen = await JourneyData.ExtraIdAsync("Premium kitchen");
        var add = buyer.GetByTestId("add-extra-" + kitchen);
        await ReloadUntilAsync(buyer, add, TimeSpan.FromMinutes(1));
        log.Step("extras unlocked in My Home (relay → SNS → homebuild-extras → Extras consumer)");

        await add.GetByRole(AriaRole.Button).ClickAsync();
        await buyer.GetByRole(AriaRole.Button, new() { Name = "Pay for extras" }).ClickAsync();
        await CompleteCheckoutAsync(buyer, "£6,500.00");
        log.Step("Stripe Checkout reached for £6,500.00; checkout.session.completed delivered");

        var line = await JourneyData.SingleLineIdAsync(PlotThree);
        var state = buyer.GetByTestId("line-" + line).GetByTestId("state");
        await ReloadUntilTextAsync(buyer, state, "Paid", TimeSpan.FromMinutes(1)); // the webhook, through stripe listen
        log.Step("\"Paid\" after the webhook");
        await ReloadUntilTextAsync(buyer, state, "Ordered on site", TimeSpan.FromMinutes(1)); // the change-order worker
        log.Step("\"Ordered on site\" after the change-order worker");

        await using var staff = await ContextAsync("site-manager.tenant-a@example.com");
        var board = await staff.NewPageAsync();
        await board.GotoAsync($"{RealSignInTests.WebUrl}/staff/developments/{Riverside}/board");
        await Assertions.Expect(board.GetByTestId("board-root")).ToHaveAttributeAsync("data-interactive", "true");
        var order = board.GetByTestId("change-orders").Locator("li", new() { HasText = "Premium kitchen" }).Filter(new() { HasText = "Plot P3:" });
        await order.GetByRole(AriaRole.Button, new() { Name = "Acknowledge" }).ClickAsync();
        await order.GetByRole(AriaRole.Button, new() { Name = "Mark fitted" }).ClickAsync();
        await Assertions.Expect(order).ToContainTextAsync("Fitted");
        await ReloadUntilTextAsync(buyer, state, "Fitted", TimeSpan.FromSeconds(30));
        log.Step("\"Fitted\" after the site manager acknowledged and fitted it");
    }

    // A snag with a photo through its whole two-party life: report (with a photo from S3 through CloudFront), acknowledge,
    // schedule, dispute, reschedule, fix, confirm, close; then handover, and two years and a day later on the test clock a
    // new snag is refused and the buyer is pointed to maintenance requests.
    [Fact]
    public async Task Buyer_and_aftercare_resolve_a_snag_with_a_photo()
    {
        Assert.SkipUnless(RealSignInTests.Enabled && AwsEnabled, "Set HOMEBUILD_COGNITO_TESTS=1 and HOMEBUILD_AWS_TESTS=1 to run against the local stack and AWS.");
        var log = Log.Start();
        using var api = new HttpClient { BaseAddress = new Uri(ApiUrl) };
        await JourneyData.ResetPlotThreeAsync();
        var buyer = await ReservePlotThreeAsync();
        await buyer.GetByRole(AriaRole.Button, new() { Name = "Pay deposit" }).First.ClickAsync(); // handover needs a confirmed sale
        await CompleteCheckoutAsync(buyer, "£5,000.00");
        await ReloadUntilAsync(buyer, buyer.Locator("article", new() { HasText = "Plot P3, Riverside" }).GetByText("Confirmed", new() { Exact = true }), TimeSpan.FromSeconds(30));
        log.Step("plot 3 reserved through the UI and its deposit confirmed by the webhook");

        var area = "Kitchen " + Guid.NewGuid().ToString("N")[..6];
        await buyer.GotoAsync(RealSignInTests.WebUrl + "/my-home");
        var form = buyer.GetByTestId("new-snag");
        if (await form.Locator("#snag-unit").CountAsync() > 0)
        {
            await form.Locator("#snag-unit").SelectOptionAsync(PlotThree);
        }

        await form.Locator("#snag-area").FillAsync(area);
        await form.Locator("#snag-description").FillAsync("Crack in the ceiling");
        await form.GetByRole(AriaRole.Button, new() { Name = "Save as draft" }).ClickAsync();
        var card = buyer.Locator("li[data-testid^=snag-]", new() { HasText = area });
        await Assertions.Expect(card).ToContainTextAsync("Draft");
        var snag = (await card.GetAttributeAsync("data-testid"))!["snag-".Length..];

        await card.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "crack.png", MimeType = "image/png", Buffer = Png });
        await card.GetByRole(AriaRole.Button, new() { Name = "Upload photo" }).ClickAsync();
        await Assertions.Expect(card.GetByTestId("snag-photo-status")).ToHaveTextAsync(new Regex("Photo added"), new() { Timeout = 30_000 });
        await buyer.ReloadAsync();
        await PhotoLoadsAsync(buyer.Locator($"[data-testid=snag-{snag}]").GetByTestId("snag-photo"));
        log.Step("photo uploaded to S3 and shown through a CloudFront signed URL");
        await Buyer(buyer, snag).GetByRole(AriaRole.Button, new() { Name = "Report to Aftercare" }).ClickAsync();
        await Assertions.Expect(Buyer(buyer, snag)).ToContainTextAsync("Reported");

        await using var staffContext = await ContextAsync("aftercare.tenant-a@example.com");
        var staff = await staffContext.NewPageAsync();
        await staff.GotoAsync(RealSignInTests.WebUrl + "/staff/aftercare");
        await PhotoLoadsAsync(Staff(staff, snag).GetByTestId("snag-photo")); // Aftercare sees the buyer's photo too
        await StaffActAsync(staff, snag, "Acknowledge", "Acknowledged");
        await ScheduleAsync(staff, snag, DateTime.UtcNow.AddDays(7));
        await StaffActAsync(staff, snag, "Mark fixed", "Fixed");
        log.Step("Aftercare acknowledged, scheduled and fixed");

        await buyer.ReloadAsync();
        await Buyer(buyer, snag).Locator($"#reason-{snag}").FillAsync("The crack is still there");
        await Buyer(buyer, snag).GetByRole(AriaRole.Button, new() { Name = "Dispute" }).ClickAsync();
        await Assertions.Expect(Buyer(buyer, snag)).ToContainTextAsync("Disputed");
        log.Step("buyer disputed the fix");

        await staff.ReloadAsync();
        await ScheduleAsync(staff, snag, DateTime.UtcNow.AddDays(10));
        await StaffActAsync(staff, snag, "Mark fixed", "Fixed");
        await buyer.ReloadAsync();
        await Buyer(buyer, snag).GetByRole(AriaRole.Button, new() { Name = "Confirm it is fixed" }).ClickAsync();
        await Assertions.Expect(Buyer(buyer, snag)).ToContainTextAsync("Confirmed");
        await staff.ReloadAsync();
        await StaffActAsync(staff, snag, "Close", "Closed");
        log.Step("rescheduled, fixed, confirmed by the buyer, closed");

        // Handover, then the test clock moves two years and a day: the defects period is over.
        await staff.Locator("#handover-unit").FillAsync(PlotThree);
        await staff.Locator("input[name=keys]").CheckAsync();
        await staff.Locator("#meters").FillAsync("Gas 00012");
        await staff.Locator("input[name=warranties]").CheckAsync();
        await staff.GetByRole(AriaRole.Button, new() { Name = "Save checklist" }).ClickAsync();
        await buyer.ReloadAsync();
        await Assertions.Expect(buyer.GetByTestId("handover-" + PlotThree)).ToContainTextAsync("Defects are covered until");
        try
        {
            (await api.PostAsJsonAsync("/dev/clock", new { by = TimeSpan.FromDays(731) }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
            log.Step("handover done; test clock advanced 731 days");
            await buyer.ReloadAsync();
            var again = buyer.GetByTestId("new-snag");
            if (await again.Locator("#snag-unit").CountAsync() > 0)
            {
                await again.Locator("#snag-unit").SelectOptionAsync(PlotThree);
            }

            await again.Locator("#snag-area").FillAsync("Hall");
            await again.Locator("#snag-description").FillAsync("Door sticks");
            await again.GetByRole(AriaRole.Button, new() { Name = "Save as draft" }).ClickAsync();
            await Assertions.Expect(buyer.GetByTestId("pay-problem")).ToContainTextAsync("Please raise a maintenance request instead.");
            log.Step("new snag refused after the defects period");
        }
        finally
        {
            (await api.DeleteAsync(new Uri("/dev/clock", UriKind.Relative), CancellationToken.None)).EnsureSuccessStatusCode(); // leave the clock as it was
        }
    }

    // A 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    private static ILocator Buyer(IPage page, string snag) => page.GetByTestId("snag-" + snag);

    private static ILocator Staff(IPage page, string snag) => page.GetByTestId("staff-snag-" + snag);

    private static async Task StaffActAsync(IPage staff, string snag, string button, string state)
    {
        await Staff(staff, snag).GetByRole(AriaRole.Button, new() { Name = button }).ClickAsync();
        await Assertions.Expect(Staff(staff, snag)).ToContainTextAsync(state);
    }

    private static async Task ScheduleAsync(IPage staff, string snag, DateTime visit)
    {
        await Staff(staff, snag).Locator($"#sub-{snag}").FillAsync("Northgate Plastering");
        await Staff(staff, snag).Locator($"#visit-{snag}").FillAsync(visit.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await StaffActAsync(staff, snag, "Schedule visit", "Scheduled");
    }

    /// <summary>The image really loaded (CloudFront accepted the signature): a decoded width, not a broken image.</summary>
    private static async Task PhotoLoadsAsync(ILocator image)
    {
        await Assertions.Expect(image).ToHaveAttributeAsync("src", new Regex(@"^https://[a-z0-9]+\.cloudfront\.net/snags/.+Signature="));
        await Assertions.Expect(image).ToHaveJSPropertyAsync("complete", true);
        Assert.True(await image.EvaluateAsync<int>("img => img.naturalWidth") > 0, "the signed URL did not load");
    }

    private async Task<IBrowserContext> ContextAsync(string user) =>
        await browser.Browser.NewContextAsync(new() { StorageStatePath = await SignedInState.ForAsync(browser.Browser, user) });

    /// <summary>The buyer reserves plot 3 through the unit page (C6's journey) and lands on My Home.</summary>
    private async Task<IPage> ReservePlotThreeAsync()
    {
        var context = await ContextAsync("buyer.tenant-a@example.com");
        var page = await context.NewPageAsync();
        await page.GotoAsync(RealSignInTests.WebUrl + "/units/" + PlotThree);
        await page.Locator("#deposit").FillAsync("5000");
        await page.Locator("#terms").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Reserve" }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex("/my-home/reservations/[0-9a-f-]{36}$"));
        await page.GotoAsync(RealSignInTests.WebUrl + "/my-home");
        return page;
    }

    /// <summary>
    /// The buyer reaches Stripe's hosted Checkout (test mode) for <paramref name="amount"/>. The page is Stripe's, with its
    /// payment form in a cross-origin frame and bot protection (hCaptcha), so the journey does not drive it: it delivers
    /// the checkout.session.completed event Stripe would send, built from the real session (read with the Stripe CLI)
    /// and signed with the local webhook secret, as stripe listen would forward it. Then it goes back to My Home.
    /// </summary>
    private static async Task CompleteCheckoutAsync(IPage page, string amount)
    {
        await page.WaitForURLAsync(new Regex(@"^https://checkout\.stripe\.com/.+/cs_test_"), new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByText(amount).First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var sessionId = Regex.Match(page.Url, "cs_test_[A-Za-z0-9]+").Value;
        var session = JsonNode.Parse(await JourneyData.RunAsync(Path.Combine(JourneyData.RepoRoot(), "tools", "stripe"), "checkout", "sessions", "retrieve", sessionId))!.AsObject();
        session["status"] = "complete";
        session["payment_status"] = "paid";
        session["payment_intent"] = "pi_e2e_" + Guid.NewGuid().ToString("N")[..16];
        var payload = new JsonObject
        {
            ["id"] = "evt_e2e_" + Guid.NewGuid().ToString("N"), ["object"] = "event", ["api_version"] = "2026-10-28.endive" /* Stripe.net 53.1.0's StripeConfiguration.ApiVersion (ConstructEvent checks it) */, ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["livemode"] = false, ["type"] = "checkout.session.completed", ["data"] = new JsonObject { ["object"] = session },
        }.ToJsonString();
        var secret = await JourneyData.WebhookSecretAsync();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiUrl + "/webhooks/stripe")) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={Convert.ToHexStringLower(mac)}");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"webhook: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        await page.GotoAsync(RealSignInTests.WebUrl + "/my-home");
    }

    private static async Task ReloadUntilAsync(IPage page, ILocator locator, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (await locator.CountAsync() == 0)
        {
            Assert.True(DateTime.UtcNow < until, $"{locator} did not appear within {within.TotalSeconds} s");
            await page.WaitForTimeoutAsync(1_000);
            await page.ReloadAsync(); // My Home is static SSR: it refreshes on navigation
        }
    }

    private static async Task ReloadUntilTextAsync(IPage page, ILocator locator, string text, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (await locator.CountAsync() == 0 || (await locator.TextContentAsync())?.Trim() != text)
        {
            Assert.True(DateTime.UtcNow < until, $"\"{text}\" did not appear within {within.TotalSeconds} s (now \"{(await locator.CountAsync() > 0 ? await locator.TextContentAsync() : "absent")}\")");
            await page.WaitForTimeoutAsync(1_000);
            await page.ReloadAsync();
        }
    }

    private sealed class Log(Stopwatch clock)
    {
        public static Log Start() => new(Stopwatch.StartNew());

        public void Step(string text) => TestContext.Current.TestOutputHelper?.WriteLine($"+{clock.Elapsed.TotalSeconds:F1}s {text}");
    }
}

/// <summary>Local database set-up for the phase 3 and 4 journeys (docker compose's PostgreSQL, the owner role).</summary>
internal static class JourneyData
{
    private const string Tenant = "0198a000-0000-7000-8000-00000000000a";
    private const string Riverside = "0198a000-0000-7000-8000-000000000001";

    /// <summary>Plot 3 as new (no reservation, extras progress, basket, change order, snag or handover), and Riverside assigned to the tenant's site managers.</summary>
    public static async Task ResetPlotThreeAsync()
    {
        const string unit = PhaseThreeFourJourneyTests.PlotThree;
        await PsqlAsync($"""
            DELETE FROM aftercare.snag_timeline WHERE snag_id IN (SELECT id FROM aftercare.snags WHERE unit_id = '{unit}');
            DELETE FROM aftercare.snags WHERE unit_id = '{unit}';
            DELETE FROM aftercare.handovers WHERE unit_id = '{unit}';
            DELETE FROM construction.change_orders WHERE unit_id = '{unit}';
            DELETE FROM extras.basket_lines WHERE basket_id IN (SELECT id FROM extras.baskets WHERE unit_id = '{unit}');
            DELETE FROM extras.baskets WHERE unit_id = '{unit}';
            DELETE FROM extras.unit_progress WHERE unit_id = '{unit}';
            DELETE FROM sales.reservations WHERE unit_id = '{unit}';
            UPDATE sales.units SET status = 'Available' WHERE id = '{unit}';
            INSERT INTO construction.site_assignments (site_manager, development_id, tenant_id)
              SELECT m.subject, '{Riverside}', m.tenant_id FROM accounts.tenant_memberships m
              WHERE m.tenant_id = '{Tenant}' AND 'site-manager' = ANY (m.roles)
              ON CONFLICT DO NOTHING; -- the tenant's site managers (the Cognito users) look after Riverside, as the seeded one does
            """);
    }

    /// <summary>
    /// MilestoneCompleted in Construction's outbox, as completing the milestone writes it (the board has no completion
    /// control): the API's relay publishes it to SNS and homebuild-extras delivers it to the Extras consumer.
    /// </summary>
    public static async Task MilestoneCompletedAsync(string unitId, string milestone)
    {
        var version = int.Parse(await PsqlAsync($"SELECT version FROM construction.build_progress WHERE unit_id = '{unitId}';"), CultureInfo.InvariantCulture);
        var payload = JsonSerializer.Serialize(new { unitId = Guid.Parse(unitId), tenantId = Guid.Parse(Tenant), milestone, completedAt = DateOnly.FromDateTime(DateTime.UtcNow), version, developmentId = Guid.Parse(Riverside) });
        await PsqlAsync($"INSERT INTO construction.outbox_messages (id, type, payload, tenant_id, occurred_at, attempts) VALUES ('{Guid.CreateVersion7()}', 'MilestoneCompleted', '{payload}', '{Tenant}', now(), 0);");
    }

    public static async Task<Guid> ExtraIdAsync(string name) =>
        Guid.Parse(await PsqlAsync($"SELECT id FROM extras.extras WHERE name = '{name}' AND development_id = '{Riverside}';"));

    public static async Task<Guid> SingleLineIdAsync(string unitId) =>
        Guid.Parse(await PsqlAsync($"SELECT l.id FROM extras.basket_lines l JOIN extras.baskets b ON b.id = l.basket_id WHERE b.unit_id = '{unitId}';"));

    private static Task<string> PsqlAsync(string sql) =>
        RunAsync(Path.Combine(RepoRoot(), "scripts", "local-psql.sh"), "-q", "-t", "-A", "-v", "ON_ERROR_STOP=1", "-c", sql);

    /// <summary>The API's Payments:WebhookSecret from its user-secrets (the one stripe listen signs with). Never printed.</summary>
    public static async Task<string> WebhookSecretAsync() =>
        (await RunAsync("dotnet", "user-secrets", "list", "--project", Path.Combine(RepoRoot(), "src", "HomeBuild.Api")))
            .Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("Payments:WebhookSecret = ", StringComparison.Ordinal))["Payments:WebhookSecret = ".Length..];

    public static async Task<string> RunAsync(string file, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args)
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(file)} {args.FirstOrDefault()} failed: {error}");
        return output.Trim();
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "HomeBuild.slnx")))
        {
            dir = dir.Parent;
        }

        return dir.FullName;
    }
}
