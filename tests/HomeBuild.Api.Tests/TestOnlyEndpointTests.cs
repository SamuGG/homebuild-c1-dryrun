using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 7: the test-only endpoints (the clock, the consumer pause) exist only in Debug builds. CI builds and tests in
/// Release, so there this test proves they are compiled out: even in the Development environment they answer 404. In a
/// Debug build it proves the same requests reach them, so the 404 in Release means "absent", not "wrong URL".
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class TestOnlyEndpointTests(PostgresFixture database)
{
    private static readonly bool Release = typeof(Program).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Release";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Test_only_endpoints_are_absent_from_release_build()
    {
        await using var api = await database.CreateApiAsync();
        await using var development = api.WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = development.CreateClient();

        using var clock = await client.PostAsJsonAsync("/dev/clock", new { by = "00:00:00" }, Ct);
        using var pause = await client.PostAsync(new Uri("/dev/consumers/change-orders/resume", UriKind.Relative), null, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"{(Release ? "Release" : "Debug")} build: POST /dev/clock → {(int)clock.StatusCode}, POST /dev/consumers/change-orders/resume → {(int)pause.StatusCode}");

        var expected = Release ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        Assert.Equal(expected, clock.StatusCode);
        Assert.Equal(expected, pause.StatusCode);
    }
}
