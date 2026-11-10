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
