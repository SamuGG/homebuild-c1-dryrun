using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Messaging;

/// <summary>
/// C9 Lab 5: the last version of an aggregate a consumer has applied. Standard SQS queues do not keep order, so an
/// event can arrive after a newer one for the same aggregate; the consumer applies an event only if its version is newer
/// than the one recorded here. Order is kept per aggregate only: events for different aggregates never wait for each other.
/// </summary>
public sealed class AggregateVersion
{
    public required string Consumer { get; init; }

    public required Guid AggregateId { get; init; }

    public required int Version { get; set; }

    public required DateTimeOffset AppliedAt { get; set; }
}

public static class AggregateVersions
{
    public const string Table = "aggregate_versions";

    public static void HasAggregateVersions(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AggregateVersion>(version =>
        {
            version.ToTable(Table);
            version.HasKey(v => new { v.Consumer, v.AggregateId });
            version.Property(v => v.Consumer).HasMaxLength(100);
        });
    }

    /// <summary>
    /// Moves <paramref name="consumer"/>'s version of the aggregate to <paramref name="version"/>, in the caller's
    /// transaction, if it is newer than the one recorded. False if the event is older than (or as old as) one already
    /// applied: the caller ignores it. One statement, so two processors racing on the same aggregate cannot both win:
    /// the second waits for the first's row lock, then re-checks the condition (Read Committed).
    /// </summary>
    public static async Task<bool> TryAdvanceAsync(DbContext db, string consumer, Guid aggregateId, int version, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var schema = db.Model.GetDefaultSchema() ?? throw new InvalidOperationException("The context has no default schema.");
        // The schema and table come from the model, never from input.
        var table = schema + "." + Table;
        var sql = "INSERT INTO " + table + " (consumer, aggregate_id, version, applied_at) VALUES ({0}, {1}, {2}, {3}) " +
                  "ON CONFLICT (consumer, aggregate_id) DO UPDATE SET version = excluded.version, applied_at = excluded.applied_at " +
                  "WHERE " + table + ".version < excluded.version";
        return await db.Database.ExecuteSqlRawAsync(sql, [consumer, aggregateId, version, at], cancellationToken) == 1;
    }
}
