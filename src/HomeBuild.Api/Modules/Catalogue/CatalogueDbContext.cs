using HomeBuild.Api.Data;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>Catalogue's tables, in schema <c>catalogue</c>. Catalogue owns a unit's description, price and specification.</summary>
internal sealed class CatalogueDbContext(DbContextOptions<CatalogueDbContext> options) : DbContext(options)
{
    public const string Schema = "catalogue";

    public DbSet<Development> Developments => Set<Development>();

    public DbSet<Unit> Units => Set<Unit>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddStronglyTypedIds();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Development>(development =>
        {
            development.HasKey(d => d.Id);
            development.Property(d => d.Name).HasMaxLength(200);
            development.Property(d => d.Town).HasMaxLength(100);
            development.Property(d => d.DepositStrategy).HasMaxLength(20);
        });

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
            unit.ComplexProperty(u => u.Specification, specification =>
            {
                specification.ToJson();
                specification.Property(x => x.Bedrooms);
                specification.Property(x => x.Bathrooms);
                specification.Property(x => x.Balcony);
                specification.PrimitiveCollection(x => x.Finishes);
            });
            unit.Ignore(u => u.Status); // availability belongs to Sales
            unit.HasOne<Development>().WithMany().HasForeignKey(u => u.DevelopmentId);
        });
    }

    internal static async Task SeedAsync(DbContext db, CancellationToken cancellationToken)
    {
        var context = (CatalogueDbContext)db;
        if (await context.Developments.AnyAsync(cancellationToken))
        {
            return;
        }

        context.Developments.AddRange(SeedData.Developments().Select(d => new Development(d.Id, d.Name, d.Town, d.DepositStrategy)));
        context.Units.AddRange(SeedData.Units());
        await context.SaveChangesAsync(cancellationToken);
    }
}
