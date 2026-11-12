using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Amazon.S3.Model;
using HomeBuild.Domain;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>Where floor plans go. Unset in CI and tests: the upload endpoints then answer 503.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string? BucketName { get; set; }

    /// <summary>The role the API assumes to sign uploads and move files (homebuild-api-uploads); its only permissions.</summary>
    public string? UploadRoleArn { get; set; }

    public string Region { get; set; } = "eu-west-2";
}

/// <summary>A pre-signed POST: the browser posts <see cref="Fields"/> and the file to <see cref="Url"/>.</summary>
internal sealed record UploadTicket(Uri Url, IReadOnlyDictionary<string, string> Fields, string Key, long MaxBytes, string ContentType, DateTimeOffset ExpiresAt);

internal abstract record MoveResult;

internal sealed record Moved(string Key) : MoveResult;

internal sealed record UploadMissing : MoveResult;

internal sealed record UploadRejected(string Reason) : MoveResult;

/// <summary>
/// Floor plans in S3 (C5 Lab 7). The browser uploads to incoming/{tenant}/{unit}/{guid}.pdf with a pre-signed POST
/// whose policy fixes the key, the content type and the size; the API then checks the object and moves it to
/// floor-plans/. The S3 client uses the assumed upload role, so a bug here cannot reach anything else.
/// </summary>
internal sealed partial class FloorPlanStorage(IOptions<StorageOptions> options, TimeProvider time) : IDisposable
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const string PdfContentType = "application/pdf";
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    private readonly Lazy<AmazonS3Client> client = new(() => CreateClient(options.Value));

    public bool IsConfigured => !string.IsNullOrEmpty(options.Value.BucketName);

    public static string IncomingKey(Guid tenantId, UnitId unitId, Guid file) => $"incoming/{tenantId}/{unitId.Value}/{file}.pdf";

    /// <summary>True only for an upload key this tenant was given for this unit: no other tenant, unit or folder.</summary>
    public static bool IsOwnIncomingKey(string key, Guid tenantId, UnitId unitId) =>
        IncomingKeyPattern().Match(key) is { Success: true } m
        && Guid.Parse(m.Groups["tenant"].Value) == tenantId
        && Guid.Parse(m.Groups["unit"].Value) == unitId.Value;

    public async Task<UploadTicket> CreateUploadAsync(Guid tenantId, UnitId unitId, TimeSpan? expiresIn = null)
    {
        var key = IncomingKey(tenantId, unitId, Guid.CreateVersion7());
        var expires = time.GetUtcNow() + (expiresIn ?? Expiry);
        var request = new CreatePresignedPostRequest
        {
            BucketName = options.Value.BucketName,
            Key = key,
            Expires = expires.UtcDateTime,
            Fields = new Dictionary<string, string> { ["Content-Type"] = PdfContentType },
            Conditions =
            [
                S3PostCondition.ExactMatch("Content-Type", PdfContentType),
                S3PostCondition.ContentLengthRange(1, MaxBytes),
            ],
        };
        var response = await client.Value.CreatePresignedPostAsync(request);
        return new UploadTicket(new Uri(response.Url), response.Fields, key, MaxBytes, PdfContentType, expires);
    }

    /// <summary>Checks an uploaded object (size and type as S3 stored them) and moves it to floor-plans/.</summary>
    public async Task<MoveResult> CheckAndMoveAsync(string incomingKey, CancellationToken cancellationToken)
    {
        var bucket = options.Value.BucketName;
        GetObjectMetadataResponse head;
        try
        {
            head = await client.Value.GetObjectMetadataAsync(bucket, incomingKey, cancellationToken);
        }
        catch (AmazonS3Exception e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            // Without s3:ListBucket, S3 answers 403 rather than 404 for a missing key.
            return new UploadMissing();
        }

        string? problem = head.ContentLength > MaxBytes ? $"The file is {head.ContentLength} bytes; the limit is {MaxBytes}."
            : head.Headers.ContentType != PdfContentType ? $"The file is {head.Headers.ContentType}, not {PdfContentType}."
            : null;
        if (problem is null)
        {
            var destination = "floor-plans/" + incomingKey["incoming/".Length..];
            await client.Value.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket, SourceKey = incomingKey, DestinationBucket = bucket, DestinationKey = destination,
            }, cancellationToken);
            await client.Value.DeleteObjectAsync(bucket, incomingKey, cancellationToken);
            return new Moved(destination);
        }

        await client.Value.DeleteObjectAsync(bucket, incomingKey, cancellationToken);
        return new UploadRejected(problem);
    }

    public void Dispose()
    {
        if (client.IsValueCreated)
        {
            client.Value.Dispose();
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

    [GeneratedRegex(@"^incoming/(?<tenant>[0-9a-f-]{36})/(?<unit>[0-9a-f-]{36})/[0-9a-f-]{36}\.pdf$")]
    private static partial Regex IncomingKeyPattern();
}
