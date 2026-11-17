using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Amazon.S3.Model;
using HomeBuild.Api.Modules.Catalogue;
using HomeBuild.Domain;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Construction;

/// <summary>
/// Diary photos in S3, with C5's direct upload: the browser posts the photo straight to S3 with a pre-signed POST
/// whose policy fixes the key (diary/{tenant}/{unit}/{guid}.jpg), the content type and the size. Same bucket and
/// upload role as floor plans (<see cref="StorageOptions"/>). Unset in CI and tests: the endpoint answers 503.
/// </summary>
internal sealed partial class DiaryPhotoStorage(IOptions<StorageOptions> options, TimeProvider time) : IDisposable
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const string JpegContentType = "image/jpeg";
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    private readonly Lazy<AmazonS3Client> client = new(() => CreateClient(options.Value));

    public bool IsConfigured => !string.IsNullOrEmpty(options.Value.BucketName);

    public static string Key(Guid tenantId, UnitId unitId, Guid file) => $"diary/{tenantId}/{unitId.Value}/{file}.jpg";

    /// <summary>True only for a diary photo key of this tenant and unit: a diary entry cannot point at anything else.</summary>
    public static bool IsOwnKey(string key, Guid tenantId, UnitId unitId) =>
        KeyPattern().Match(key) is { Success: true } m
        && Guid.Parse(m.Groups["tenant"].Value) == tenantId
        && Guid.Parse(m.Groups["unit"].Value) == unitId.Value;

    public async Task<(Uri Url, IReadOnlyDictionary<string, string> Fields, string Key, DateTimeOffset ExpiresAt)> CreateUploadAsync(Guid tenantId, UnitId unitId)
    {
        var key = Key(tenantId, unitId, Guid.CreateVersion7());
        var expires = time.GetUtcNow() + Expiry;
        var response = await client.Value.CreatePresignedPostAsync(new CreatePresignedPostRequest
        {
            BucketName = options.Value.BucketName,
            Key = key,
            Expires = expires.UtcDateTime,
            Fields = new Dictionary<string, string> { ["Content-Type"] = JpegContentType },
            Conditions =
            [
                S3PostCondition.ExactMatch("Content-Type", JpegContentType),
                S3PostCondition.ContentLengthRange(1, MaxBytes),
            ],
        });
        return (new Uri(response.Url), response.Fields, key, expires);
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

    [GeneratedRegex(@"^diary/(?<tenant>[0-9a-f-]{36})/(?<unit>[0-9a-f-]{36})/[0-9a-f-]{36}\.jpg$")]
    private static partial Regex KeyPattern();
}
