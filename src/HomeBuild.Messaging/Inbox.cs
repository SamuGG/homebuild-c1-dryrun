using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Messaging;

/// <summary>
/// C9 Lab 3: a message a consumer has processed. The key is (consumer, message ID): the outbox row's ID, carried in the
/// <c>message_id</c> attribute, which stays the same when the relay publishes a row twice (SQS's own message IDs differ).
/// </summary>
public sealed class InboxMessage
{
    public required string Consumer { get; init; }

    public required Guid MessageId { get; init; }

    public required DateTimeOffset ProcessedAt { get; init; }
}

public static class Inbox
{
    public const string Table = "inbox_messages";

    public static void HasInbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<InboxMessage>(inbox =>
        {
            inbox.ToTable(Table);
            inbox.HasKey(m => new { m.Consumer, m.MessageId }); // unique per consumer: the deduplication guard
            inbox.Property(m => m.Consumer).HasMaxLength(100);
        });
    }

    /// <summary>
    /// Records the message as processed by <paramref name="consumer"/>, in the caller's transaction. False if it already
    /// was (a duplicate): the caller does nothing and the message is deleted.
    /// </summary>
    public static async Task<bool> TryRecordAsync(DbContext db, string consumer, Guid messageId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var schema = db.Model.GetDefaultSchema() ?? throw new InvalidOperationException("The context has no default schema.");
        // The schema comes from the model, never from input.
        var sql = "INSERT INTO " + schema + "." + Table + " (consumer, message_id, processed_at) VALUES ({0}, {1}, {2}) ON CONFLICT DO NOTHING";
        return await db.Database.ExecuteSqlRawAsync(sql, [consumer, messageId, at], cancellationToken) == 1;
    }
}
