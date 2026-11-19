using System.Text.Json;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Modules.Notifications;

/// <summary>What the notifications Lambda writes (C9 Lab 7): one JSON object per event.</summary>
internal sealed record StoredNotification(Guid MessageId, string Type, Guid TenantId, Guid UnitId, Guid? ReservationId, Guid? BuyerId, string Text, DateTimeOffset CreatedAt, int? Version);

internal sealed record MyNotificationResponse(Guid Id, string Type, Guid UnitId, string Text, DateTimeOffset CreatedAt);

public sealed class NotificationsOptions
{
    public const string Section = "Notifications";

    /// <summary>The storage bucket the Lambda writes to (notifications/ prefix). Unset: My Home has no notifications.</summary>
    public string? BucketName { get; set; }
}

/// <summary>Port: read the notifications under a key prefix (S3 in the API, in memory in tests).</summary>
internal interface INotificationReader
{
    Task<IReadOnlyList<StoredNotification>> ReadAsync(string prefix, int max, CancellationToken cancellationToken);
}

internal sealed class S3NotificationReader(IOptions<NotificationsOptions> options) : INotificationReader, IDisposable
{
    private readonly Lazy<AmazonS3Client> _s3 = new(() => new AmazonS3Client(RegionEndpoint.EUWest2));

    public async Task<IReadOnlyList<StoredNotification>> ReadAsync(string prefix, int max, CancellationToken cancellationToken)
    {
        if (options.Value.BucketName is not { Length: > 0 } bucket)
        {
            return [];
        }

        var listed = await _s3.Value.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, MaxKeys = max }, cancellationToken);
        var notifications = new List<StoredNotification>();
        foreach (var item in (listed.S3Objects ?? []).OrderByDescending(o => o.LastModified).Take(max))
        {
            using var response = await _s3.Value.GetObjectAsync(bucket, item.Key, cancellationToken);
            if (await JsonSerializer.DeserializeAsync<StoredNotification>(response.ResponseStream, JsonSerializerOptions.Web, cancellationToken) is { } n)
            {
                notifications.Add(n);
            }
        }

        return notifications;
    }

    public void Dispose()
    {
        if (_s3.IsValueCreated)
        {
            _s3.Value.Dispose();
        }
    }
}

/// <summary>
/// C9 Lab 7: GET /me/notifications. The Lambda writes each notification to S3; the API reads the buyer's own prefix
/// (notifications/{tenant}/{buyer}/) and, for events that name a unit but no buyer (milestones, the contract pack),
/// the prefix of each unit the buyer holds or has bought (notifications/{tenant}/units/{unit}/). Newest first.
/// </summary>
internal static class MyNotificationsEndpoint
{
    private const int Max = 50;

    public static void MapMyNotifications(this RouteGroupBuilder group) =>
        group.MapGet("/me/notifications", Handle)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("ListMyNotifications")
            .WithSummary("The signed-in buyer's notifications: progress, the contract pack, an ended hold (v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanReadOwnProgress);

    private static async Task<Results<Ok<List<MyNotificationResponse>>, ForbidHttpResult>> Handle(
        IUnitAvailability sales, INotificationReader notifications, ITenantContext tenant, HttpContext http, CancellationToken cancellationToken)
    {
        if (http.User.CallerBuyerId() is not { } buyer || tenant.TenantId is not { } tenantId)
        {
            return TypedResults.Forbid();
        }

        var units = (await sales.ReservationsOfAsync(buyer, cancellationToken))
            .Where(r => r.State is nameof(Held) or nameof(Confirmed)).Select(r => r.UnitId.Value).Distinct();
        var prefixes = units.Select(u => $"notifications/{tenantId}/units/{u}/").Prepend($"notifications/{tenantId}/{buyer.Value}/");
        var found = new List<StoredNotification>();
        foreach (var prefix in prefixes)
        {
            found.AddRange(await notifications.ReadAsync(prefix, Max, cancellationToken));
        }

        return TypedResults.Ok(found.Where(n => n.TenantId == tenantId).OrderByDescending(n => n.CreatedAt).Take(Max)
            .Select(n => new MyNotificationResponse(n.MessageId, n.Type, n.UnitId, n.Text, n.CreatedAt)).ToList());
    }
}
