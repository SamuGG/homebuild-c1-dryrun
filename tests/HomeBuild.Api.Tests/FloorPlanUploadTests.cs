using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C5 Lab 7: floor plans uploaded straight to S3 with a pre-signed POST. The tests marked "S3" upload to the real
/// bucket and are opt-in: HOMEBUILD_AWS_TESTS=1 with AWS credentials and Storage:BucketName / Storage:UploadRoleArn
/// (user-secrets or Storage__* environment variables). The tenant check needs no AWS and always runs.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class FloorPlanUploadTests(PostgresFixture database) : IAsyncLifetime
{
    private static readonly bool AwsEnabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";
    private static readonly Guid DemoTenant = Guid.Parse("0198a000-0000-7000-8000-00000000000a");
    private static readonly Guid OtherTenant = Guid.Parse("0198a000-0000-7000-8000-00000000000b");
    private static readonly string Plot1 = EndpointTests.RiversidePlot1;

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Access(TestTokens.Users.Staff("admin").ToString(), "admin"));
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    public static TheoryData<string, string> ForeignKeys => new()
    {
        { "other tenant", $"incoming/{OtherTenant}/{EndpointTests.RiversidePlot1}/{Guid.CreateVersion7()}.pdf" },
        { "other unit", $"incoming/{DemoTenant}/{EndpointTests.RiversidePlot2}/{Guid.CreateVersion7()}.pdf" },
        { "outside incoming/", $"floor-plans/{DemoTenant}/{EndpointTests.RiversidePlot1}/{Guid.CreateVersion7()}.pdf" },
        { "path traversal", $"incoming/{DemoTenant}/{EndpointTests.RiversidePlot1}/../../{OtherTenant}/x.pdf" },
    };

    [Theory]
    [MemberData(nameof(ForeignKeys))]
    public async Task Key_for_other_tenant_is_refused_by_API(string why, string key)
    {
        var response = await _admin.PostAsJsonAsync($"/v2/units/{Plot1}/floor-plan", new { key }, Ct);
        var problem = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{why}: {response.StatusCode}");
        Assert.Equal("Not your upload", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Upload_over_10MB_is_rejected_by_S3()
    {
        Assert.SkipUnless(AwsEnabled, "S3: set HOMEBUILD_AWS_TESTS=1.");
        var ticket = await TicketAsync();

        using var response = await PostToS3Async(ticket, new byte[FloorPlanStorage.MaxBytes + 1], ticket.Fields["Content-Type"]);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("<Code>EntityTooLarge</Code>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_with_wrong_content_type_is_rejected()
    {
        Assert.SkipUnless(AwsEnabled, "S3: set HOMEBUILD_AWS_TESTS=1.");
        var ticket = await TicketAsync();

        using var response = await PostToS3Async(ticket, Pdf(), "text/plain");
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Invalid according to Policy: Policy Condition failed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_presigned_post_is_rejected()
    {
        Assert.SkipUnless(AwsEnabled, "S3: set HOMEBUILD_AWS_TESTS=1.");
        var storage = _factory.Services.GetRequiredService<FloorPlanStorage>();
        var ticket = await storage.CreateUploadAsync(DemoTenant, new UnitId(Guid.Parse(Plot1)), expiresIn: TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        using var response = await PostToS3Async(new(ticket.Url, ticket.Fields, ticket.Key), Pdf(), ticket.ContentType);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Invalid according to Policy: Policy expired", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_pdf_is_uploaded_checked_and_moved_to_floor_plans()
    {
        Assert.SkipUnless(AwsEnabled, "S3: set HOMEBUILD_AWS_TESTS=1.");
        var ticket = await TicketAsync();
        Assert.StartsWith($"incoming/{DemoTenant}/{Plot1}/", ticket.Key, StringComparison.Ordinal);
        Assert.True(ticket.ExpiresAt - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5));

        using var upload = await PostToS3Async(ticket, Pdf(), ticket.Fields["Content-Type"]);
        var complete = await _admin.PostAsJsonAsync($"/v2/units/{Plot1}/floor-plan", new { key = ticket.Key }, Ct);
        var again = await _admin.PostAsJsonAsync($"/v2/units/{Plot1}/floor-plan", new { key = ticket.Key }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("floor-plans/" + ticket.Key["incoming/".Length..],
            JsonElement.Parse(await complete.Content.ReadAsStringAsync(Ct)).GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode); // moved: nothing left under incoming/
    }

    private sealed record Ticket(Uri Url, IReadOnlyDictionary<string, string> Fields, string Key, DateTimeOffset ExpiresAt = default);

    private async Task<Ticket> TicketAsync()
    {
        var response = await _admin.PostAsync(new Uri($"/v2/units/{Plot1}/floor-plan/uploads", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
        return new Ticket(
            new Uri(json.GetProperty("url").GetString()!),
            json.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
            json.GetProperty("key").GetString()!,
            json.GetProperty("expiresAt").GetDateTimeOffset());
    }

    // What a browser sends: the policy's fields first, then the file.
    private static async Task<HttpResponseMessage> PostToS3Async(Ticket ticket, byte[] file, string contentType)
    {
        using var s3 = new HttpClient();
        using var form = new MultipartFormDataContent();
        foreach (var (name, value) in ticket.Fields.Where(f => f.Key != "Content-Type"))
        {
            form.Add(new StringContent(value), name);
        }

        form.Add(new StringContent(contentType), "Content-Type");
        form.Add(new ByteArrayContent(file), "file", "plan.pdf");
        return await s3.PostAsync(ticket.Url, form, Ct);
    }

    private static byte[] Pdf() => "%PDF-1.4\n1 0 obj <<>> endobj\ntrailer <<>>\n%%EOF\n"u8.ToArray();
}
