using HomeBuild.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Accounts;

/// <summary>Which tenant (and HomeBuild roles) a Cognito user belongs to. HomeBuild decides tenancy, not the token.</summary>
internal sealed record TenantMembership(string Subject, Guid TenantId, string[] Roles);

/// <summary>
/// Accounts' tables, in schema <c>accounts</c>. Not tenant-filtered and not under row-level security: the membership is
/// read to find the tenant, before any tenant is known. The app role may only read it.
/// </summary>
internal sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public const string Schema = "accounts";

    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<TenantMembership>(membership =>
        {
            membership.ToTable("tenant_memberships");
            membership.HasKey(m => m.Subject); // one tenant per user for now
            membership.Property(m => m.Subject).HasMaxLength(128);
            membership.Property(m => m.TenantId);
            membership.Property(m => m.Roles);
            membership.HasIndex(m => m.TenantId);
        });
    }

    internal static async Task SeedAsync(DbContext db, CancellationToken cancellationToken)
    {
        var context = (AccountsDbContext)db;
        if (await context.TenantMemberships.AnyAsync(cancellationToken))
        {
            return;
        }

        // The demo buyer that the Bruno collection reserves for (C2's fixed data belongs to the demo tenant).
        context.TenantMemberships.Add(new TenantMembership(SeedData.DemoBuyer, SeedData.DemoTenant, ["buyer"]));
        context.TenantMemberships.Add(new TenantMembership(SeedData.DemoBuyer2, SeedData.DemoTenant, ["buyer"]));
        await context.SaveChangesAsync(cancellationToken);
    }
}
