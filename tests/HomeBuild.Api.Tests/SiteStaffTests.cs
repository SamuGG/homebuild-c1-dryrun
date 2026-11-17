using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Tests;

/// <summary>C7 Lab 4: who may complete milestones, correct them and write the site diary, and the audit trail.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class SiteStaffTests(PostgresFixture database) : IAsyncLifetime
{
    // Riverside plot 1 has Foundations done and Frame in progress (seeded from its ID); Hillcrest plot 1 the same.
    private const string RiversidePlot1 = "0198a000-0000-7000-8001-000000000001";
    private const string HillcrestPlot1 = "0198a000-0000-7000-8002-000000000001";
    private static readonly DateOnly Today = new(2026, 11, 17);

    private ApiFactory _factory = null!;
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;
    private HttpClient _anonymous = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        // No S3 here, whatever the developer's user-secrets say (C5's bucket may be configured locally): uploads answer 503.
        _host = _factory.WithWebHostBuilder(b => b.UseSetting("Storage:BucketName", ""));
        _client = _host.CreateClient();
        _anonymous = _host.Server.CreateClient(); // no default token
        _anonymous.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenants.Demo.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _anonymous.Dispose();
        await _host.DisposeAsync();
        await _factory.DisposeAsync();
    }

    // One row per role and action, on a Riverside unit. "assigned" is the site manager assigned to Riverside;
    // "other-site" is a site manager assigned to Hillcrest only.
    public static TheoryData<string, string, HttpStatusCode> RoleActionTable => new()
    {
        { "assigned", "complete", HttpStatusCode.OK },
        { "assigned", "correct", HttpStatusCode.Forbidden },
        { "assigned", "diary", HttpStatusCode.Created },
        { "assigned", "photo", HttpStatusCode.ServiceUnavailable }, // allowed; S3 is not configured in tests
        { "other-site", "complete", HttpStatusCode.Forbidden },
        { "other-site", "correct", HttpStatusCode.Forbidden },
        { "other-site", "diary", HttpStatusCode.Forbidden },
        { "other-site", "photo", HttpStatusCode.Forbidden },
        { "admin", "complete", HttpStatusCode.Forbidden },
        { "admin", "correct", HttpStatusCode.OK },
        { "admin", "diary", HttpStatusCode.Forbidden },
        { "admin", "photo", HttpStatusCode.Forbidden },
        { "buyer", "complete", HttpStatusCode.Forbidden },
        { "buyer", "correct", HttpStatusCode.Forbidden },
        { "buyer", "diary", HttpStatusCode.Forbidden },
        { "buyer", "photo", HttpStatusCode.Forbidden },
        { "sales-agent", "complete", HttpStatusCode.Forbidden },
        { "sales-agent", "correct", HttpStatusCode.Forbidden },
        { "sales-agent", "diary", HttpStatusCode.Forbidden },
        { "sales-agent", "photo", HttpStatusCode.Forbidden },
        { "finance", "complete", HttpStatusCode.Forbidden },
        { "finance", "correct", HttpStatusCode.Forbidden },
        { "finance", "diary", HttpStatusCode.Forbidden },
        { "finance", "photo", HttpStatusCode.Forbidden },
        { "aftercare", "complete", HttpStatusCode.Forbidden },
        { "aftercare", "correct", HttpStatusCode.Forbidden },
        { "aftercare", "diary", HttpStatusCode.Forbidden },
        { "aftercare", "photo", HttpStatusCode.Forbidden },
        { "anonymous", "complete", HttpStatusCode.Unauthorized },
        { "anonymous", "correct", HttpStatusCode.Unauthorized },
        { "anonymous", "diary", HttpStatusCode.Unauthorized },
        { "anonymous", "photo", HttpStatusCode.Unauthorized },
    };

    [Theory]
    [MemberData(nameof(RoleActionTable))]
    public async Task Role_and_action_table_is_enforced(string who, string action, HttpStatusCode expected)
    {
        using var request = Action(action, RiversidePlot1, who);

        using var response = await (who == "anonymous" ? _anonymous : _client).SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Correction_requires_reason_and_is_audited()
    {
        // The Riverside site manager completes Frame; an admin then corrects its date, first without a reason.
        using (var complete = Action("complete", RiversidePlot1, "assigned"))
        {
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(complete, Ct)).StatusCode);
        }

        using var noReason = Request(HttpMethod.Post, $"/v2/units/{RiversidePlot1}/milestones/Frame/correction", "admin");
        noReason.Content = JsonContent.Create(new { completedOn = Today.AddDays(-2), reason = "  " });
        using var noReasonResponse = await _client.SendAsync(noReason, Ct);
        using var withReason = Request(HttpMethod.Post, $"/v2/units/{RiversidePlot1}/milestones/Frame/correction", "admin");
        withReason.Content = JsonContent.Create(new { completedOn = Today.AddDays(-2), reason = "Signed off on site on Saturday; entered on Monday." });
        using var withReasonResponse = await _client.SendAsync(withReason, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, noReasonResponse.StatusCode);
        Assert.Contains("\"reason\"", await noReasonResponse.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, withReasonResponse.StatusCode);
        await using var db = ConstructionModule.CreateDbContext(_factory.ConnectionString, TestTenants.Demo);
        var audit = await db.MilestoneAudits.Where(a => a.UnitId == new UnitId(Guid.Parse(RiversidePlot1))).OrderBy(a => a.At).ToListAsync(Ct);
        Assert.Equal(["Completed", "Corrected"], audit.Select(a => a.Action));
        Assert.Equal(TestTokens.Users.Staff("site-manager").ToString(), audit[0].By);
        Assert.Equal((Milestone.Frame, TestTokens.Users.Staff("admin").ToString(), (DateOnly?)Today, Today.AddDays(-2), "Signed off on site on Saturday; entered on Monday."),
            (audit[1].Milestone, audit[1].By, audit[1].From, audit[1].To, audit[1].Reason));
        var progress = await db.BuildProgress.AsNoTracking().SingleAsync(p => p.UnitId == new UnitId(Guid.Parse(RiversidePlot1)), Ct);
        Assert.Equal(Today.AddDays(-2), progress[Milestone.Frame].CompletedOn);
        Assert.Equal(TestTokens.Users.Staff("site-manager").ToString(), progress[Milestone.Frame].CompletedBy); // who completed it stays
    }

    [Fact]
    public async Task Each_site_manager_acts_only_on_their_own_development()
    {
        using var ownHillcrest = Action("complete", HillcrestPlot1, "other-site");
        using var riversideOnHillcrest = Action("complete", HillcrestPlot1, "assigned");

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(ownHillcrest, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(riversideOnHillcrest, Ct)).StatusCode);
    }

    [Fact]
    public async Task Development_in_the_body_is_ignored()
    {
        // A forged assignment: the Hillcrest site manager names Hillcrest in the body of a Riverside unit's request.
        using var request = Request(HttpMethod.Post, $"/v2/units/{RiversidePlot1}/milestones/Frame/completion", "other-site");
        request.Content = JsonContent.Create(new { completedOn = Today, developmentId = "0198a000-0000-7000-8000-000000000002" });

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Milestone_rule_refusal_is_422_and_not_audited()
    {
        using var request = Request(HttpMethod.Post, $"/v2/units/{RiversidePlot1}/milestones/Roof/completion", "assigned");
        request.Content = JsonContent.Create(new { completedOn = Today });

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Roof cannot complete before Frame", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        await using var db = ConstructionModule.CreateDbContext(_factory.ConnectionString, TestTenants.Demo);
        Assert.False(await db.MilestoneAudits.AnyAsync(Ct));
    }

    [Fact]
    public async Task Diary_entry_rejects_photo_keys_outside_its_own_diary_prefix()
    {
        using var request = Request(HttpMethod.Post, $"/v2/units/{RiversidePlot1}/diary", "assigned");
        request.Content = JsonContent.Create(new
        {
            writtenOn = Today, text = "Roof trusses delivered.", weather = "Dry, 9°C",
            photoKeys = new[] { $"floor-plans/{TestTenants.Demo}/{RiversidePlot1}/0198a000-0000-7000-8000-0000000000aa.pdf" },
        });

        using var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"photoKeys\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static HttpRequestMessage Action(string action, string unit, string who)
    {
        var (method, path, body) = action switch
        {
            "complete" => (HttpMethod.Post, $"/v2/units/{unit}/milestones/Frame/completion", (object)new { completedOn = Today }),
            "correct" => (HttpMethod.Post, $"/v2/units/{unit}/milestones/Foundations/correction", new { completedOn = new DateOnly(2026, 6, 20), reason = "Date typed wrongly" }),
            "diary" => (HttpMethod.Post, $"/v2/units/{unit}/diary", new { writtenOn = Today, text = "Frame inspected.", weather = "Sunny" }),
            "photo" => (HttpMethod.Post, $"/v2/units/{unit}/diary/photos/uploads", new { }),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var request = Request(method, path, who);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string who)
    {
        var request = new HttpRequestMessage(method, path);
        var (subject, role) = who switch
        {
            "anonymous" => (null, null),
            "assigned" => (TestTokens.Users.Staff("site-manager"), "site-manager"),
            "other-site" => (TestTokens.Users.HillcrestSiteManager, "site-manager"),
            "buyer" => (TestTokens.Users.Owner, "buyer"),
            _ => ((Guid?)TestTokens.Users.Staff(who), who),
        };
        if (subject is { } sub)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(sub.ToString(), role!));
        }

        return request;
    }
}
