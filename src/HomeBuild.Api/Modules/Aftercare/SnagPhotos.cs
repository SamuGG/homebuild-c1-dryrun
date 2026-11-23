using System.Text.RegularExpressions;
using Amazon;
using Amazon.CloudFront;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Amazon.S3.Model;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Aftercare;

/// <summary>
/// C10 Lab 5: how snag photos are served. The distribution reads the bucket through origin access control (the bucket
/// accepts reads from it only) and serves nothing without a signed URL from this key group's private key.
/// </summary>
public sealed class CloudFrontOptions
{
    public const string Section = "CloudFront";

    /// <summary>The distribution's domain, e.g. d111111abcdef8.cloudfront.net.</summary>
    public string? Domain { get; set; }

    /// <summary>The public key's ID in the key group (CloudFront calls it Key-Pair-Id in the URL).</summary>
    public string? KeyPairId { get; set; }

    /// <summary>The RSA private key, PEM: user-secrets locally, SSM SecureString in AWS. Never logged.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>How long a signed URL works (the brief: 10 minutes).</summary>
    public TimeSpan UrlLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public bool IsConfigured => Domain is { Length: > 0 } && KeyPairId is { Length: > 0 } && PrivateKey is { Length: > 0 };
}

internal sealed record SnagPhotoUploadResponse(Uri Url, IReadOnlyDictionary<string, string> Fields, string Key, long MaxBytes, string ContentTypePrefix, DateTimeOffset ExpiresAt);

internal sealed record SnagPhotoKeyRequest(string Key);

/// <param name="Url">A CloudFront signed URL that works until <paramref name="ExpiresAt"/>.</param>
internal sealed record SnagPhotoResponse(string Key, string AddedBy, DateTimeOffset AddedAt, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Snag photos in S3 under snags/{tenant}/{snag}/{guid}: C5's direct upload (pre-signed POST), images only, 5 MB.</summary>
internal sealed partial class SnagPhotoStorage(IOptions<StorageOptions> storage, IOptions<CloudFrontOptions> cloudFront, TimeProvider time) : IDisposable
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public static readonly TimeSpan UploadExpiry = TimeSpan.FromMinutes(5);

    private readonly Lazy<AmazonS3Client> _client = new(() => CreateClient(storage.Value));

    public bool CanUpload => !string.IsNullOrEmpty(storage.Value.BucketName);

    public bool CanServe => cloudFront.Value.IsConfigured;

    public static string Prefix(Guid tenantId, Guid snagId) => $"snags/{tenantId}/{snagId}/";

    /// <summary>True only for a key under this snag's own prefix: a photo entry cannot point at anything else.</summary>
    public static bool IsOwnKey(string key, Guid tenantId, Guid snagId) =>
        KeyPattern().Match(key) is { Success: true } m && Guid.Parse(m.Groups["tenant"].Value) == tenantId && Guid.Parse(m.Groups["snag"].Value) == snagId;

    public async Task<SnagPhotoUploadResponse> CreateUploadAsync(Guid tenantId, Guid snagId)
    {
        var key = Prefix(tenantId, snagId) + Guid.CreateVersion7();
        var expires = time.GetUtcNow() + UploadExpiry;
        var response = await _client.Value.CreatePresignedPostAsync(new CreatePresignedPostRequest
        {
            BucketName = storage.Value.BucketName,
            Key = key,
            Expires = expires.UtcDateTime,
            Conditions =
            [
                S3PostCondition.StartsWith("Content-Type", "image/"), // images only
                S3PostCondition.ContentLengthRange(1, MaxBytes),
            ],
        });
        return new SnagPhotoUploadResponse(new Uri(response.Url), response.Fields, key, MaxBytes, "image/", expires);
    }

    /// <summary>A canned-policy signed URL for one object, valid for CloudFront:UrlLifetime.</summary>
    public (Uri Url, DateTimeOffset ExpiresAt) Sign(string key)
    {
        var o = cloudFront.Value;
        var expires = time.GetUtcNow() + o.UrlLifetime;
        using var privateKey = new StringReader(o.PrivateKey!);
        var url = AmazonCloudFrontUrlSigner.GetCannedSignedURL($"https://{o.Domain}/{key}", privateKey, o.KeyPairId!, expires.UtcDateTime);
        return (new Uri(url), expires);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static AmazonS3Client CreateClient(StorageOptions options)
    {
        AWSCredentials credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials();
        if (!string.IsNullOrEmpty(options.UploadRoleArn))
        {
            credentials = new AssumeRoleAWSCredentials(credentials, options.UploadRoleArn, "homebuild-api-uploads");
        }

        return new AmazonS3Client(credentials, RegionEndpoint.GetBySystemName(options.Region));
    }

    [GeneratedRegex(@"^snags/(?<tenant>[0-9a-f-]{36})/(?<snag>[0-9a-f-]{36})/[0-9a-f-]{36}$")]
    private static partial Regex KeyPattern();
}

/// <summary>
/// C10 Lab 5's endpoints. Every one first checks who may see the snag (its buyer, or Aftercare): anyone else gets 404,
/// before any storage or signing happens (BOLA, C4).
/// </summary>
internal static class SnagPhotoEndpoints
{
    public static void MapSnagPhotos(this RouteGroupBuilder group)
    {
        group.MapPost("/snags/{id:guid}/photos/uploads", StartUploadAsync).MapToApiVersion(ApiVersions.V2).WithName("StartSnagPhotoUpload")
            .WithSummary("A pre-signed POST for a snag photo (an image up to 5 MB, 5 minutes) straight to S3 (v2)")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable).RequireAuthorization(Policies.CanActOnSnags);
        group.MapPost("/snags/{id:guid}/photos", AddAsync).MapToApiVersion(ApiVersions.V2).WithName("AddSnagPhoto")
            .WithSummary("Record an uploaded photo in the snag's timeline (v2)").RequireAuthorization(Policies.CanActOnSnags);
        group.MapGet("/snags/{id:guid}/photos", ListAsync).MapToApiVersion(ApiVersions.V2).WithName("ListSnagPhotos")
            .WithSummary("The snag's photos as CloudFront signed URLs, valid for 10 minutes (v2)")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable).RequireAuthorization(Policies.CanActOnSnags);
    }

    private static async Task<Results<Ok<SnagPhotoUploadResponse>, NotFound, ProblemHttpResult>> StartUploadAsync(
        Guid id, AftercareDbContext db, IUnitAvailability sales, SnagPhotoStorage storage, HttpContext http, CancellationToken cancellationToken)
    {
        if (await SnagAccess.LoadAsync(db, sales, id, http, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        return storage.CanUpload
            ? TypedResults.Ok(await storage.CreateUploadAsync(db.CurrentTenantId, id))
            : TypedResults.Problem(Problems.Business(http, "storage-not-configured", "Photo uploads unavailable", StatusCodes.Status503ServiceUnavailable, "Storage:BucketName is not set."));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> AddAsync(
        Guid id, SnagPhotoKeyRequest request, AftercareDbContext db, IUnitAvailability sales, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (await SnagAccess.LoadAsync(db, sales, id, http, cancellationToken) is not { } loaded)
        {
            return TypedResults.NotFound();
        }

        if (!SnagPhotoStorage.IsOwnKey(request.Key, db.CurrentTenantId, id))
        {
            return TypedResults.Problem(Problems.Business(http, "photo-key", "Not this snag's photo", StatusCodes.Status422UnprocessableEntity,
                "The key must be one this snag's upload was signed for."));
        }

        loaded.Snag.AddPhoto(loaded.Actor, loaded.ActorId, request.Key, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<List<SnagPhotoResponse>>, NotFound, ProblemHttpResult>> ListAsync(
        Guid id, AftercareDbContext db, IUnitAvailability sales, SnagPhotoStorage storage, HttpContext http, CancellationToken cancellationToken)
    {
        if (await SnagAccess.LoadAsync(db, sales, id, http, cancellationToken) is not { } loaded)
        {
            return TypedResults.NotFound(); // another buyer's snag: no signed URL, not even a hint it exists
        }

        if (!storage.CanServe)
        {
            return TypedResults.Problem(Problems.Business(http, "cloudfront-not-configured", "Photos unavailable", StatusCodes.Status503ServiceUnavailable, "CloudFront is not configured."));
        }

        return TypedResults.Ok(loaded.Snag.Timeline.Where(e => e.Kind == SnagEntryKind.Photo).OrderBy(e => e.At).Select(e =>
        {
            var (url, expires) = storage.Sign(e.Text);
            return new SnagPhotoResponse(e.Text, e.Actor.ToString(), e.At, url, expires);
        }).ToList());
    }
}
