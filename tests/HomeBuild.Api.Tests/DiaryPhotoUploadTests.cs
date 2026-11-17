using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBuild.Api.Modules.Construction;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C7 Lab 4: site diary photos reuse C5's direct-to-S3 upload under diary/. The key checks always run; the uploads
/// need C5's storage stack and are opt-in, like FloorPlanUploadTests: HOMEBUILD_AWS_TESTS=1 with AWS credentials and
/// Storage:BucketName / Storage:UploadRoleArn.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class DiaryPhotoUploadTests(PostgresFixture database) : IAsyncLifetime
{
    private static readonly bool AwsEnabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";
    private const string Plot1 = "0198a000-0000-7000-8001-000000000001";
    private const string Plot2 = "0198a000-0000-7000-8001-000000000002";

    private ApiFactory _factory = null!;
    private HttpClient _siteManager = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = await database.CreateApiAsync();
        _siteManager = _factory.CreateClient();
        _siteManager.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Access(TestTokens.Users.Staff("site-manager").ToString(), "site-manager"));
    }

    public async ValueTask DisposeAsync()
    {
        _siteManager.Dispose();
        await _factory.DisposeAsync();
    }

    public static TheoryData<string, string> ForeignKeys => new()
    {
        { "other tenant", $"diary/{TestTenants.Other}/{Plot1}/{Guid.CreateVersion7()}.jpg" },
        { "other unit", $"diary/{TestTenants.Demo}/{Plot2}/{Guid.CreateVersion7()}.jpg" },
        { "outside diary/", $"floor-plans/{TestTenants.Demo}/{Plot1}/{Guid.CreateVersion7()}.jpg" },
        { "path traversal", $"diary/{TestTenants.Demo}/{Plot1}/../../{TestTenants.Other}/x.jpg" },
    };

    [Theory]
    [MemberData(nameof(ForeignKeys))]
    public async Task Diary_entry_refuses_a_photo_key_it_was_not_given(string why, string key)
    {
        var response = await _siteManager.PostAsJsonAsync($"/v2/units/{Plot1}/diary",
            new { writtenOn = "2026-11-17", text = "Scaffold up.", weather = "Windy", photoKeys = new[] { key } }, Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{why}: {response.StatusCode}");
    }

    [Fact]
    public void Own_diary_key_is_accepted_by_the_key_check()
    {
        var key = DiaryPhotoStorage.Key(TestTenants.Demo, new(Guid.Parse(Plot1)), Guid.CreateVersion7());

        Assert.StartsWith("diary/", key, StringComparison.Ordinal);
        Assert.True(DiaryPhotoStorage.IsOwnKey(key, TestTenants.Demo, new(Guid.Parse(Plot1))));
    }

    [Fact]
    public async Task Diary_photo_uploads_under_diary_and_the_entry_keeps_its_key()
    {
        Assert.SkipUnless(AwsEnabled, "S3: set HOMEBUILD_AWS_TESTS=1.");
        var (url, fields, key) = await TicketAsync();

        using var upload = await PostToS3Async(url, fields, "\xFF\xD8\xFF\xE0 tiny jpeg"u8.ToArray(), DiaryPhotoStorage.JpegContentType);
        var entry = await _siteManager.PostAsJsonAsync($"/v2/units/{Plot1}/diary",
            new { writtenOn = "2026-11-17", text = "Roof felt on.", weather = "Dry", photoKeys = new[] { key } }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        Assert.StartsWith($"diary/{TestTenants.Demo}/{Plot1}/", key, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
    }

    [Fact]
    public async Task Diary_photo_over_10MB_is_rejected_by_S3()
    {
        Assert.SkipUnless(AwsEnabled, "S3: set HOMEBUILD_AWS_TESTS=1.");
        var (url, fields, _) = await TicketAsync();

        using var response = await PostToS3Async(url, fields, new byte[DiaryPhotoStorage.MaxBytes + 1], DiaryPhotoStorage.JpegContentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("<Code>EntityTooLarge</Code>", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<(Uri Url, Dictionary<string, string> Fields, string Key)> TicketAsync()
    {
        using var response = await _siteManager.PostAsync($"/v2/units/{Plot1}/diary/photos/uploads", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonElement.Parse(await response.Content.ReadAsStringAsync(Ct));
        return (new Uri(json.GetProperty("url").GetString()!),
            json.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
            json.GetProperty("key").GetString()!);
    }

    private static async Task<HttpResponseMessage> PostToS3Async(Uri url, Dictionary<string, string> fields, byte[] file, string contentType)
    {
        using var s3 = new HttpClient();
        using var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields.Where(f => f.Key != "Content-Type"))
        {
            form.Add(new StringContent(value), name);
        }

        form.Add(new StringContent(contentType), "Content-Type");
        form.Add(new ByteArrayContent(file), "file", "photo.jpg");
        return await s3.PostAsync(url, form, Ct);
    }
}
