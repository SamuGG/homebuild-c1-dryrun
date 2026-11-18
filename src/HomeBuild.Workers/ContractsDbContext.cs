using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Workers;

/// <summary>An issued contract pack: one per reservation, the PDF's S3 key, and the message that caused it.</summary>
internal sealed class ContractRow
{
    public required Guid ReservationId { get; init; }

    public required Guid UnitId { get; init; }

    public required Guid TenantId { get; init; }

    public required string DocumentKey { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required Guid MessageId { get; init; }
}

/// <summary>
/// C9 Lab 3: the contract worker's tables, schema <c>contracts</c>: issued contracts, the consumer's inbox, and an outbox
/// for ContractIssued. The worker owns this schema; it reads no module's tables (everything it needs is in the message).
/// </summary>
internal sealed class ContractsDbContext(DbContextOptions<ContractsDbContext> options) : DbContext(options)
{
    public const string Schema = "contracts";

    public DbSet<ContractRow> Contracts => Set<ContractRow>();

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history", Schema)
                .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorCodesToAdd: null))
            .UseSnakeCaseNamingConvention();

    public static ContractsDbContext Create(string connectionString) =>
        new((DbContextOptions<ContractsDbContext>)Configure(new DbContextOptionsBuilder<ContractsDbContext>(), connectionString).Options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<ContractRow>(contract =>
        {
            contract.ToTable("contracts");
            contract.HasKey(c => c.ReservationId); // one contract pack per reservation
            contract.Property(c => c.DocumentKey).HasMaxLength(300);
            contract.HasIndex(c => c.TenantId);
        });
        modelBuilder.HasInbox();
        modelBuilder.HasOutbox();
    }
}
