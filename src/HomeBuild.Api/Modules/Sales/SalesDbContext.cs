using HomeBuild.Api.Data;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>
/// Sales' tables, in schema <c>sales</c>. Sales owns availability and reservations, so it keeps its own copy of
/// each unit's price and status and never reads Catalogue's tables.
/// </summary>
internal sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options)
{
    public const string Schema = "sales";
    public const string ActiveReservationIndex = "ux_reservations_one_active_per_unit";

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddStronglyTypedIds();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Unit>(unit =>
        {
            unit.HasKey(u => u.Id);
            // C1's domain types expose get-only properties; EF maps those only when named explicitly.
            unit.Property(u => u.DevelopmentId);
            unit.Property(u => u.PlotNumber).HasMaxLength(20);
            unit.ComplexProperty(u => u.Price, price =>
            {
                price.Property(m => m.Amount).HasPrecision(12, 2);
                price.Property(m => m.Currency).HasMaxLength(3).IsFixedLength();
            });
            unit.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
            unit.Ignore(u => u.Specification); // Catalogue's concern
            // Optimistic concurrency: Npgsql maps a uint row version to PostgreSQL's xmin system column (no schema change).
            // A shadow property, so the domain type stays free of persistence concerns.
            unit.Property<uint>("Version").IsRowVersion();
            // Lab 6, units by development and status: both are equality filters; development_id first because it is
            // far more selective (250 rows of 50,000) and also serves "all units of a development" on its own.
            unit.HasIndex(u => new { u.DevelopmentId, u.Status }).HasDatabaseName("ix_units_development_id_status");
        });

        modelBuilder.Entity<Reservation>(reservation =>
        {
            reservation.HasKey(r => r.Id);
            reservation.Property(r => r.UnitId);
            reservation.Property(r => r.BuyerId);
            reservation.Property(r => r.CreatedAt);
            reservation.Property(r => r.ExpiresAt);
            reservation.ComplexProperty(r => r.Deposit, deposit =>
            {
                deposit.Property(m => m.Amount).HasPrecision(12, 2);
                deposit.Property(m => m.Currency).HasMaxLength(3).IsFixedLength();
            });
            reservation.Property(r => r.State).HasConversion<ReservationStateConverter>().HasMaxLength(60);
            // The state's kind as its own column, generated from "Kind|timestamp", so the database can index and constrain it.
            reservation.Property<string>("Status").HasMaxLength(20)
                .HasComputedColumnSql("lower(split_part(state, '|', 1))", stored: true);
            reservation.HasOne<Unit>().WithMany().HasForeignKey(r => r.UnitId);
            // The final guard: at most one active (held or confirmed) reservation per unit, whatever the code does.
            reservation.HasIndex(r => r.UnitId)
                .IsUnique()
                .HasFilter("status IN ('held', 'confirmed')")
                .HasDatabaseName(ActiveReservationIndex);
            // Lab 6, held reservations expiring soon: a partial index on expires_at holds only held rows,
            // so the range scan reads no expired, confirmed or cancelled reservations and returns rows in sort order.
            reservation.HasIndex(r => r.ExpiresAt).HasFilter("status = 'held'").HasDatabaseName("ix_reservations_held_expires_at");
            // Lab 6, a buyer's reservations newest first: equality on buyer_id, then created_at descending for the ORDER BY.
            reservation.HasIndex(r => new { r.BuyerId, r.CreatedAt }).IsDescending(false, true)
                .HasDatabaseName("ix_reservations_buyer_id_created_at");
        });
    }

    internal static async Task SeedAsync(DbContext db, CancellationToken cancellationToken)
    {
        var context = (SalesDbContext)db;
        if (await context.Units.AnyAsync(cancellationToken))
        {
            return;
        }

        context.Units.AddRange(SeedData.Units());
        await context.SaveChangesAsync(cancellationToken);
    }
}
