using System.Net;
using System.Net.Http.Headers;
using Amazon;
using Amazon.S3;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using static HomeBuild.Api.Tests.AftercareTests;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 5 against the real bucket and distribution, opt-in: HOMEBUILD_AWS_TESTS=1, HOMEBUILD_STORAGE_BUCKET,
/// HOMEBUILD_UPLOAD_ROLE_ARN, HOMEBUILD_PHOTOS_DOMAIN and HOMEBUILD_PHOTOS_KEY_ID (HomeBuild-Storage's outputs), AWS_PROFILE.
/// The private key is read from SSM (SecureString), never from a file. Each test uploads a tiny PNG through the
/// pre-signed POST, as the browser does, and deletes it afterwards.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class RealSnagPhotoTests(PostgresFixture database)
{
    public const string PrivateKeyParameter = "/homebuild/cloudfront/snag-photos-private-key";
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HOMEBUILD_AWS_TESTS") == "1";

    // A 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The bucket answers 403 to a plain URL for the photo (no ACL or policy lets the public read it), while CloudFront
    // serves it with a signed URL and refuses it without one.
    [Fact]
    public async Task Direct_s3_url_is_denied()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1 and the HomeBuild-Storage outputs.");
        await using var photo = await UploadedPhotoAsync(TimeSpan.FromMinutes(10));
        using var web = new HttpClient();

        using var direct = await web.GetAsync(new Uri($"https://{Bucket}.s3.eu-west-2.amazonaws.com/{photo.Key}"), Ct);
        Log($"direct S3 GET → {(int)direct.StatusCode} {direct.StatusCode}");
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        using var unsigned = await web.GetAsync(new Uri($"https://{Domain}/{photo.Key}"), Ct);
        Log($"CloudFront GET without signature → {(int)unsigned.StatusCode} {unsigned.StatusCode}");
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        using var signed = await web.GetAsync(photo.Url, Ct);
        Log($"CloudFront GET with signature → {(int)signed.StatusCode} {signed.StatusCode}, {signed.Content.Headers.ContentType}, x-cache {string.Join(",", signed.Headers.TryGetValues("X-Cache", out var c) ? c : [])}");
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        Assert.Equal(Png, await signed.Content.ReadAsByteArrayAsync(Ct));
    }

    // The URL works while it is valid and CloudFront refuses it once Expires has passed. The lifetime is cut to 20 seconds
    // for the test (the app's is 10 minutes): the same signature, just a nearer DateLessThan.
    [Fact]
    public async Task Signed_url_works_until_expiry()
    {
        Assert.SkipUnless(Enabled, "AWS: set HOMEBUILD_AWS_TESTS=1 and the HomeBuild-Storage outputs.");
        await using var photo = await UploadedPhotoAsync(TimeSpan.FromSeconds(20));
        using var web = new HttpClient();

        using (var before = await web.GetAsync(photo.Url, Ct))
        {
            Log($"{DateTimeOffset.UtcNow:HH:mm:ss} before expiry ({photo.ExpiresAt:HH:mm:ss}) → {(int)before.StatusCode} {before.StatusCode}");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        var wait = photo.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Ct);
        using var after = await web.GetAsync(photo.Url, Ct);
        Log($"{DateTimeOffset.UtcNow:HH:mm:ss} after expiry → {(int)after.StatusCode} {after.StatusCode} {Truncate(await after.Content.ReadAsStringAsync(Ct))}");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    private static string Bucket => Env("HOMEBUILD_STORAGE_BUCKET");

    private static string Domain => Env("HOMEBUILD_PHOTOS_DOMAIN");

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name}.");

    private static void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    private static string Truncate(string text) => text.Length > 200 ? text[..200] : text;

    /// <summary>A reported snag with one photo uploaded through the API's pre-signed POST, and its signed URL.</summary>
    private async Task<UploadedPhoto> UploadedPhotoAsync(TimeSpan lifetime)
    {
        using var ssm = new AmazonSimpleSystemsManagementClient(RegionEndpoint.EUWest2);
        var privateKey = (await ssm.GetParameterAsync(new GetParameterRequest { Name = PrivateKeyParameter, WithDecryption = true }, Ct)).Parameter.Value;
        var (api, factory, client) = await StartAsync(database, b => b
            .UseSetting("Storage:BucketName", Bucket)
            .UseSetting("Storage:UploadRoleArn", Env("HOMEBUILD_UPLOAD_ROLE_ARN"))
            .UseSetting("CloudFront:Domain", Domain)
            .UseSetting("CloudFront:KeyPairId", Env("HOMEBUILD_PHOTOS_KEY_ID"))
            .UseSetting("CloudFront:PrivateKey", privateKey)
            .UseSetting("CloudFront:UrlLifetime", lifetime.ToString()));
        try
        {
            var id = await DraftAndReportAsync(client);
            var ticket = await SendAsync(client, HttpMethod.Post, $"/v2/snags/{id}/photos/uploads", Buyer);
            var key = ticket.GetProperty("key").GetString()!;
            using (var form = new MultipartFormDataContent())
            {
                foreach (var field in ticket.GetProperty("fields").EnumerateObject())
                {
                    form.Add(new StringContent(field.Value.GetString()!), field.Name);
                }

                form.Add(new StringContent("image/png"), "Content-Type");
                var file = new ByteArrayContent(Png);
                file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(file, "file", "snag.png"); // the file goes last
                using var web = new HttpClient();
                using var posted = await web.PostAsync(new Uri(ticket.GetProperty("url").GetString()!), form, Ct);
                Log($"pre-signed POST → {(int)posted.StatusCode} {posted.StatusCode}");
                Assert.True(posted.IsSuccessStatusCode, $"upload: {posted.StatusCode} {await posted.Content.ReadAsStringAsync(Ct)}");
            }

            using (var added = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/photos", Buyer, new { key }))
            {
                Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
            }

            var photo = Assert.Single((await SendAsync(client, HttpMethod.Get, $"/v2/snags/{id}/photos", Buyer)).EnumerateArray());
            var url = new Uri(photo.GetProperty("url").GetString()!);
            Log($"key {key}; signed URL {url.GetLeftPart(UriPartial.Path)}?Expires=…&Signature=…&Key-Pair-Id={Env("HOMEBUILD_PHOTOS_KEY_ID")}");
            return new UploadedPhoto(key, url, photo.GetProperty("expiresAt").GetDateTimeOffset(), api, factory, client);
        }
        catch
        {
            client.Dispose();
            await factory.DisposeAsync();
            await api.DisposeAsync();
            throw;
        }
    }

    private sealed record UploadedPhoto(string Key, Uri Url, DateTimeOffset ExpiresAt, ApiFactory Api, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using (var s3 = new AmazonS3Client(RegionEndpoint.EUWest2))
            {
                await s3.DeleteObjectAsync(Bucket, Key);
            }

            Client.Dispose();
            await Factory.DisposeAsync();
            await Api.DisposeAsync();
        }
    }
}
