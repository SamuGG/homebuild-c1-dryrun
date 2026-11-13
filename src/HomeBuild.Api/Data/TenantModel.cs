using HomeBuild.Api.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HomeBuild.Api.Data;

/// <summary>Layer 1 of tenant isolation: a tenant_id column and an EF Core query filter on every tenant-owned entity.</summary>
internal static class TenantModel
{
    public const string TenantId = "TenantId";

    /// <summary>Adds the shadow TenantId to an entity type and filters it on the context's current tenant.</summary>
    public static EntityTypeBuilder<T> HasTenant<T>(this EntityTypeBuilder<T> entity, ITenantScoped context)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity.Property<Guid>(TenantId);
        entity.HasIndex(TenantId);
        entity.HasQueryFilter(e => EF.Property<Guid>(e, TenantId) == context.CurrentTenantId);
        return entity;
    }

    /// <summary>Stamps new rows with the current tenant; a write with no tenant is a programming error.</summary>
    public static void StampTenant(this ChangeTracker changeTracker, ITenantContext tenant)
    {
        // Owned types (C7's milestones, stored in their owner's row) have no tenant of their own.
        foreach (var entry in changeTracker.Entries().Where(e => e.State == EntityState.Added && !e.Metadata.IsOwned()))
        {
            var property = entry.Property(TenantId);
            if ((Guid)property.CurrentValue! != Guid.Empty)
            {
                continue;
            }

            property.CurrentValue = tenant.TenantId
                ?? throw new InvalidOperationException($"Cannot save a new {entry.Metadata.ClrType.Name} without a tenant.");
        }
    }
}

/// <summary>A DbContext whose queries are filtered to one tenant.</summary>
internal interface ITenantScoped
{
    Guid CurrentTenantId { get; }
}
