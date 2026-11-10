using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace HomeBuild.Api.Modules.Catalogue;

/// <summary>Catalogue's tables, in schema <c>catalogue</c>. Catalogue owns a unit's description, price and specification.</summary>
internal sealed class CatalogueDbContext(DbContextOptions<CatalogueDbContext> options, ITenantContext tenant) : DbContext(options), ITenantScoped
{
    /// <summary>The tenant every query is filtered on (layer 1). Guid.Empty matches nothing.</summary>
    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public ITenantContext Tenant => tenant;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ChangeTracker.StampTenant(tenant);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.StampTenant(tenant);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public const string Schema = "catalogue";
    public const string UnitName = "Name";
    public const string UnitDescription = "Description";
    public const string UnitSearchVector = "SearchVector";

    public DbSet<Development> Developments => Set<Development>();

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<UnitPrice> UnitPrices => Set<UnitPrice>();

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
            development.HasMany(d => d.Units).WithOne().HasForeignKey(u => u.DevelopmentId);
            development.HasMany(d => d.PriceHistory).WithOne().HasForeignKey(p => p.DevelopmentId);
        });

        modelBuilder.Entity<UnitPrice>(price =>
        {
            price.HasKey(p => p.Id);
            price.Property(p => p.Amount).HasPrecision(12, 2);
            price.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            price.HasOne<Unit>().WithMany().HasForeignKey(p => p.UnitId);
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
            // Catalogue's marketing text, searched by GET /units/search. Shadow properties: the domain has no use for them.
            unit.Property<string>(UnitName).HasMaxLength(200).HasDefaultValue("");
            unit.Property<string>(UnitDescription).HasMaxLength(2000).HasDefaultValue("");
            unit.Property<NpgsqlTsVector>(UnitSearchVector)
                .HasComputedColumnSql("to_tsvector('english'::regconfig, coalesce(name, '') || ' ' || coalesce(description, ''))", stored: true);
            unit.HasIndex(UnitSearchVector).HasMethod("GIN");
            // Keyset index (development_id, price_amount, id) is created in migration UnitSearchAndKeyset with raw SQL:
            // EF Core 10 cannot index a complex-type property such as Price.Amount.
        });

        modelBuilder.Entity<Development>().HasTenant(this);
        modelBuilder.Entity<Unit>().HasTenant(this);
        modelBuilder.Entity<UnitPrice>().HasTenant(this);
    }

    internal static async Task SeedAsync(DbContext db, CancellationToken cancellationToken)
    {
        var context = (CatalogueDbContext)db;
        if (await context.Developments.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        foreach (var d in SeedData.Developments())
        {
            context.Entry(context.Developments.Add(new Development(d.Id, d.Name, d.Town, d.DepositStrategy)).Entity)
                .Property(TenantModel.TenantId).CurrentValue = SeedData.DemoTenant;
        }

        foreach (var unit in SeedData.Units())
        {
            var entry = context.Units.Add(unit);
            var (name, description) = SeedData.TextFor(unit);
            entry.Property(UnitName).CurrentValue = name;
            entry.Property(UnitDescription).CurrentValue = description;
            entry.Property(TenantModel.TenantId).CurrentValue = SeedData.DemoTenant;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
