using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using static HomeBuild.Api.Tests.AftercareTests;

namespace HomeBuild.Api.Tests;

/// <summary>
/// C10 Lab 5 without AWS: who gets a signed URL, what it looks like, and which keys a photo entry may name. The signing
/// key is a fresh RSA key made here; RealSnagPhotoTests checks the real distribution.
/// </summary>
[Collection(IntegrationDatabase.Name)]
public sealed class SnagPhotoTests(PostgresFixture database)
{
    private const string Domain = "d111111abcdef8.cloudfront.net";
    private const string KeyPairId = "K2TESTKEYPAIRID";
    private static readonly RSA Key = RSA.Create(2048);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // BOLA (C4): another buyer asking for the photos, an upload, or to add a photo to someone else's snag gets 404, as if
    // the snag did not exist, and never a signed URL.
    [Fact]
    public async Task Other_buyer_cannot_get_signed_url()
    {
        var (api, factory, client) = await StartAsync(database, Configure);
        await using var _ = api;
        await using var __ = factory;
        var id = await DraftAndReportAsync(client);
        var key = $"snags/{TestTenants.Demo}/{id}/{Guid.CreateVersion7()}";
        using (var added = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/photos", Buyer, new { key }))
        {
            Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        }

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"/v2/snags/{id}/photos", null),
                     (HttpMethod.Post, $"/v2/snags/{id}/photos/uploads", null),
                     (HttpMethod.Post, $"/v2/snags/{id}/photos", new { key }),
                 })
        {
            using var response = await RawAsync(client, method, path, OtherBuyer, body);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path}: {response.StatusCode} {text}");
            Assert.DoesNotContain("Signature", text, StringComparison.Ordinal);
        }

        Assert.Single((await SendAsync(client, HttpMethod.Get, $"/v2/snags/{id}/photos", Buyer)).EnumerateArray()); // the owner still sees it
        Assert.Single((await SendAsync(client, HttpMethod.Get, $"/v2/snags/{id}/photos", AftercareTests.Aftercare)).EnumerateArray());
    }

    // A canned-policy signed URL: the object's URL plus Expires (10 minutes on), Signature and Key-Pair-Id, where the
    // signature is RSA-SHA1 over the canned policy, in CloudFront's URL-safe base64. Checked with the public key.
    [Fact]
    public async Task Signed_url_is_canned_policy_for_ten_minutes()
    {
        var (api, factory, client) = await StartAsync(database, Configure);
        await using var _ = api;
        await using var __ = factory;
        var id = await DraftAndReportAsync(client);
        var key = $"snags/{TestTenants.Demo}/{id}/{Guid.CreateVersion7()}";
        using (var added = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/photos", AftercareTests.Aftercare, new { key }))
        {
            Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        }

        var before = DateTimeOffset.UtcNow;

        var photo = Assert.Single((await SendAsync(client, HttpMethod.Get, $"/v2/snags/{id}/photos", Buyer)).EnumerateArray());
        var url = new Uri(photo.GetProperty("url").GetString()!);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal($"https://{Domain}/{key}", url.GetLeftPart(UriPartial.Path));
        Assert.Equal(KeyPairId, query["Key-Pair-Id"].ToString());
        var expires = long.Parse(query["Expires"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires - before.ToUnixTimeSeconds(), 595, 605);
        var policy = """{"Statement":[{"Resource":""" + $"\"https://{Domain}/{key}\"" + ""","Condition":{"DateLessThan":{"AWS:EpochTime":""" + expires + "}}}]}";
        var signature = Convert.FromBase64String(query["Signature"].ToString().Replace('-', '+').Replace('_', '=').Replace('~', '/'));
        Assert.True(Key.VerifyData(Encoding.UTF8.GetBytes(policy), signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1), "signature does not verify with the public key");
    }

    // A photo entry can only name a key under this snag's own prefix: not another snag's, another tenant's, or the
    // floor plans'. Otherwise a buyer could have HomeBuild sign a URL for any object in the bucket.
    [Fact]
    public async Task Photo_key_must_be_the_snags_own()
    {
        var (api, factory, client) = await StartAsync(database, Configure);
        await using var _ = api;
        await using var __ = factory;
        var id = await DraftAndReportAsync(client);
        string[] foreign =
        [
            $"snags/{TestTenants.Demo}/{Guid.NewGuid()}/{Guid.NewGuid()}",
            $"snags/{Guid.NewGuid()}/{id}/{Guid.NewGuid()}",
            $"floor-plans/{TestTenants.Demo}/{Guid.NewGuid()}.pdf",
            $"snags/{TestTenants.Demo}/{id}/../../floor-plans/x",
        ];
        foreach (var key in foreign)
        {
            using var response = await RawAsync(client, HttpMethod.Post, $"/v2/snags/{id}/photos", Buyer, new { key });
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{key}: {response.StatusCode}");
        }

        Assert.Empty((await SendAsync(client, HttpMethod.Get, $"/v2/snags/{id}/photos", Buyer)).EnumerateArray());
    }

    private static void Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) => builder
        .UseSetting("CloudFront:Domain", Domain)
        .UseSetting("CloudFront:KeyPairId", KeyPairId)
        .UseSetting("CloudFront:PrivateKey", Key.ExportPkcs8PrivateKeyPem());
}
