using HomeBuild.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Http.Idempotency;

/// <summary>One idempotency key of one caller, with the first request's fingerprint and, once done, its response.</summary>
internal sealed class IdempotencyRecord
{
    public const string InProgress = "in_progress";
    public const string Completed = "completed";

    public required string Caller { get; init; }

    public required Guid Key { get; init; }

    public required string Fingerprint { get; init; }

    public required string Status { get; init; }

    public int? ResponseStatus { get; init; }

    public string? ResponseContentType { get; init; }

    public string? ResponseLocation { get; init; }

    public byte[]? ResponseBody { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>
/// C8 Lab 3: the idempotency_keys table, in its own schema. It is HTTP infrastructure shared by the modules' POSTs, not
/// a module's data. Keys belong to a caller (the token's subject), not a tenant, so the table has no row-level security;
/// every query names the caller.
/// </summary>
internal sealed class IdempotencyDbContext(DbContextOptions<IdempotencyDbContext> options) : DbContext(options)
{
    public const string Schema = "idempotency";

    public DbSet<IdempotencyRecord> Keys => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<IdempotencyRecord>(key =>
        {
            key.ToTable("idempotency_keys");
            key.HasKey(k => new { k.Caller, k.Key }); // unique on (caller, key): the real guard, across API instances
            key.Property(k => k.Caller).HasMaxLength(200);
            key.Property(k => k.Fingerprint).HasMaxLength(200);
            key.Property(k => k.Status).HasMaxLength(20);
            key.Property(k => k.ResponseContentType).HasMaxLength(200);
            key.Property(k => k.ResponseLocation).HasMaxLength(2000);
            key.HasIndex(k => k.CreatedAt); // for C9's cleanup job
        });
    }

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, Schema);

    internal static IdempotencyDbContext Create(string connectionString) =>
        new((DbContextOptions<IdempotencyDbContext>)Configure(new DbContextOptionsBuilder<IdempotencyDbContext>(), connectionString).Options);
}
