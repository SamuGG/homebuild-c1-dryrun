using System.Text.Json;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HomeBuild.Workers;

/// <summary>A message for the buyer of a unit, in the order the worker wrote them (<see cref="Sequence"/>).</summary>
internal sealed class BuyerNotificationRow
{
    public long Sequence { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid TenantId { get; init; }

    public required Guid UnitId { get; init; }

    public Guid? ReservationId { get; init; }

    public required string Kind { get; init; }

    public required string Text { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// C9 Lab 5: the notifications consumer's tables, schema <c>notifications</c>: the buyer messages, its inbox, and the
/// last build-progress version applied per unit. Lab 7 moves this consumer to Lambda (notifications in S3).
/// </summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    public DbSet<BuyerNotificationRow> Notifications => Set<BuyerNotificationRow>();

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", Schema)
                .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null))
            .UseSnakeCaseNamingConvention();

    public static NotificationsDbContext Create(string connectionString) =>
        new((DbContextOptions<NotificationsDbContext>)Configure(new DbContextOptionsBuilder<NotificationsDbContext>(), connectionString).Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<BuyerNotificationRow>(notification =>
        {
            notification.ToTable("buyer_notifications");
            notification.HasKey(n => n.Sequence);
            notification.Property(n => n.Sequence).UseIdentityAlwaysColumn();
            notification.HasIndex(n => n.MessageId).IsUnique();
            notification.HasIndex(n => new { n.TenantId, n.UnitId });
            notification.Property(n => n.Kind).HasMaxLength(40);
            notification.Property(n => n.Text).HasMaxLength(500);
        });
        modelBuilder.HasInbox();
        modelBuilder.HasAggregateVersions();
    }
}

internal sealed class DesignTimeNotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        NotificationsDbContext.Create(Environment.GetEnvironmentVariable("ConnectionStrings__HomeBuild") ?? "Host=localhost;Database=homebuild;Username=design-time");
}

/// <summary>
/// C9 Lab 5: writes the buyer a message for each event the notifications queue carries. A milestone is applied only if
/// its version is newer than the last one applied for the unit: a standard queue can deliver "frame" before
/// "foundations", and the older one is then ignored (the newer message already says more). Order is kept per unit
/// only; different units are processed in parallel. The inbox row, the version and the message commit together.
/// </summary>
internal sealed class NotificationWorker(NotificationsDbContext db, TimeProvider time) : IMessageHandler
{
    public const string Name = "notifications";

    public async Task HandleAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var messageId = message.MessageId ?? throw new PermanentMessageException($"Message {message.ProviderId} has no message_id attribute.");
        var notification = Read(message.Type, messageId, message.Body);
        if (notification is null)
        {
            return; // not ours (the filter policy should not send it): deleted
        }

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = time.GetUtcNow();
            var apply = await Inbox.TryRecordAsync(db, Name, messageId, now, ct)
                && (notification.Version is not { } version || await AggregateVersions.TryAdvanceAsync(db, Name, notification.UnitId, version, now, ct));
            if (apply)
            {
                db.Notifications.Add(new BuyerNotificationRow
                {
                    MessageId = messageId, TenantId = notification.TenantId, UnitId = notification.UnitId, ReservationId = notification.ReservationId,
                    Kind = notification.Kind, Text = notification.Text, CreatedAt = now,
                });
                await db.SaveChangesAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    private sealed record Notification(Guid TenantId, Guid UnitId, Guid? ReservationId, string Kind, string Text, int? Version);

    private static Notification? Read(string? type, Guid messageId, string body)
    {
        try
        {
            return type switch
            {
                IntegrationEvents.MilestoneCompleted when JsonSerializer.Deserialize<MilestoneCompletedMessage>(body, JsonSerializerOptions.Web) is { } m =>
                    new(m.TenantId, m.UnitId, null, type, $"Good news: your home has reached a new stage ({m.Milestone}) on {m.CompletedAt:d MMMM yyyy}.", m.Version),
                IntegrationEvents.ContractIssued when JsonSerializer.Deserialize<ContractIssuedMessage>(body, JsonSerializerOptions.Web) is { } c =>
                    new(c.TenantId, c.UnitId, c.ReservationId, type, "Your contract pack is ready to review in My Home.", null),
                IntegrationEvents.ReservationExpired when JsonSerializer.Deserialize<ReservationExpiredMessage>(body, JsonSerializerOptions.Web) is { } e =>
                    new(e.TenantId, e.UnitId, e.ReservationId, type, "Your reservation hold has ended because the deposit was not paid in time.", null),
                IntegrationEvents.MilestoneCompleted or IntegrationEvents.ContractIssued or IntegrationEvents.ReservationExpired =>
                    throw new PermanentMessageException($"Message {messageId} has no body."),
                _ => null,
            };
        }
        catch (JsonException e)
        {
            throw new PermanentMessageException($"Message {messageId} is not a {type} payload.", e);
        }
    }
}
