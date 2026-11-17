using HomeBuild.Api.Modules.Construction;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Api.Tests;

/// <summary>C7 Lab 3: the board's specifications give the same answer in memory and translated to SQL.</summary>
[Collection(IntegrationDatabase.Name)]
public sealed class BoardSpecificationTests(PostgresFixture database)
{
    // Milestone N of the builder's builds is planned for 1 June 2026 + 21 × (N + 1) days.
    private static readonly DateOnly Today = new(2026, 8, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Specifications => ["behind schedule", "ready for handover", "behind schedule or ready for handover", "not behind schedule"];

    [Fact]
    public async Task Behind_schedule_spec_matches_in_memory_and_in_SQL()
    {
        await using var db = await SeededAsync();
        var spec = new BehindSchedule(Today);

        var all = await db.BuildProgress.AsNoTracking().ToListAsync(Ct);
        var inMemory = all.Where(spec.IsSatisfiedBy).Select(p => p.UnitId.Value).Order().ToList();
        var inSql = (await db.BuildProgress.Where(spec.ToExpression()).Select(p => p.UnitId).ToListAsync(Ct)).Select(id => id.Value).Order().ToList();

        Assert.Equal(inMemory, inSql);
        Assert.NotEmpty(inSql);
        Assert.NotEqual(all.Count, inSql.Count);
        var sql = db.BuildProgress.Where(spec.ToExpression()).ToQueryString();
        TestContext.Current.SendDiagnosticMessage(sql);
        await LogAsync("BehindSchedule", sql);
        Assert.Contains("jsonb", sql, StringComparison.Ordinal); // filtered inside PostgreSQL, in the milestones column
    }

    [Theory]
    [MemberData(nameof(Specifications))]
    public async Task Board_specification_matches_in_memory_and_in_SQL(string name)
    {
        await using var db = await SeededAsync();
        var spec = name switch
        {
            "behind schedule" => new BehindSchedule(Today),
            "ready for handover" => BoardSpecifications.ReadyForHandover(),
            "behind schedule or ready for handover" => new BehindSchedule(Today).Or(BoardSpecifications.ReadyForHandover()),
            _ => new BehindSchedule(Today).Not(),
        };

        var inMemory = (await db.BuildProgress.AsNoTracking().ToListAsync(Ct)).Where(spec.IsSatisfiedBy).Select(p => p.UnitId.Value).Order().ToList();
        var inSql = (await db.BuildProgress.Where(spec.ToExpression()).Select(p => p.UnitId).ToListAsync(Ct)).Select(id => id.Value).Order().ToList();

        Assert.Equal(inMemory, inSql);
        Assert.NotEmpty(inSql);
        await LogAsync(name, db.BuildProgress.Where(spec.ToExpression()).ToQueryString());
    }

    [Theory]
    [InlineData("behind-schedule")]
    [InlineData("ready-for-handover")]
    public async Task Board_filter_returns_the_units_the_specification_selects(string show)
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var spec = show == "behind-schedule" ? new BehindSchedule(today) : BoardSpecifications.ReadyForHandover();
        await using var db = ConstructionModule.CreateDbContext(factory.ConnectionString, TestTenants.Demo);
        var expected = (await db.BuildProgress.AsNoTracking().Where(p => p.DevelopmentId == HomeBuild.Api.Data.SeedData.Riverside).ToListAsync(Ct))
            .Where(spec.IsSatisfiedBy).Select(p => p.UnitId.Value).Order().ToList();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/developments/{HomeBuild.Api.Data.SeedData.Riverside.Value}/board?show={show}");
        request.Headers.Authorization = new("Bearer", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));
        using var response = await client.SendAsync(request, Ct);
        var board = System.Text.Json.JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, board.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("unitId").GetGuid()).Order());
    }

    [Fact]
    public async Task Board_filter_with_an_unknown_name_is_a_field_error()
    {
        await using var factory = await database.CreateApiAsync();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/developments/{HomeBuild.Api.Data.SeedData.Riverside.Value}/board?show=late");
        request.Headers.Authorization = new("Bearer", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"show\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>The seeded units plus builds in known states: finished and clean, finished with a defect, on time, late.</summary>
    private async Task<ConstructionDbContext> SeededAsync()
    {
        var cs = PostgresFixture.AppConnectionString(await database.CreateDatabaseAsync());
        var db = ConstructionModule.CreateDbContext(cs, TestTenants.Demo);
        var finished = ABuildProgress().CompletedThrough(8).Build();
        var finishedWithDefect = ABuildProgress().CompletedThrough(8).Build();
        finishedWithDefect.Note(Milestone.SecondFix, "Snag: bathroom tiles cracked", 1);
        db.BuildProgress.AddRange(finished, finishedWithDefect, ABuildProgress().CompletedThrough(4).Build(), ABuildProgress().CompletedThrough(1).Build());
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        return db;
    }

    private static Task LogAsync(string name, string sql) =>
        Environment.GetEnvironmentVariable("SPEC_SQL_OUT") is { } path
            ? File.AppendAllTextAsync(path, $"-- {name}\n{sql}\n\n", Ct)
            : Task.CompletedTask;
}
