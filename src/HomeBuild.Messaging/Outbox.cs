using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Messaging;

/// <summary>
/// C9 Lab 2: an event waiting to be published, written in the same transaction as the change that raised it. The ID is
/// the message ID consumers deduplicate on. One <c>outbox_messages</c> table per module schema.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Type { get; init; }

    /// <summary>The event as JSON (jsonb).</summary>
    public required string Payload { get; init; }

    public required Guid TenantId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public static OutboxMessage For<T>(string type, T message, Guid tenantId, DateTimeOffset occurredAt) => new()
    {
        Type = type,
        Payload = JsonSerializer.Serialize(message, JsonSerializerOptions.Web),
        TenantId = tenantId,
        OccurredAt = occurredAt,
    };
}

public static class OutboxModel
{
    public const string Table = "outbox_messages";

    /// <summary>
    /// Maps the module's outbox in its own schema. No row-level security and no tenant filter: the relay reads every
    /// tenant's rows; the tenant travels in the row and in the message.
    /// </summary>
    public static void HasOutbox(this ModelBuilder modelBuilder) =>
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable(Table);
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Id).ValueGeneratedNever();
            outbox.Property(m => m.Type).HasMaxLength(100);
            outbox.Property(m => m.Payload).HasColumnType("jsonb");
            // The relay's query: unpublished rows, oldest first.
            outbox.HasIndex(m => m.OccurredAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_messages_unpublished");
        });
}
