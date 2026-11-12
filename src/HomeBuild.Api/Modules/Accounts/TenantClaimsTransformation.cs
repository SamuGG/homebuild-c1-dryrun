using System.Security.Claims;
using HomeBuild.Api.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Accounts;

/// <summary>
/// Adds the <c>tenant_id</c> claim from HomeBuild's membership table. The identity provider proves who the user is;
/// HomeBuild decides their tenant, so any tenant_id the token itself carries is removed first. Scoped: the lookup is
/// cached for one request only.
/// </summary>
internal sealed class TenantClaimsTransformation(AccountsDbContext db) : IClaimsTransformation
{
    private bool _looked;
    private TenantMembership? _membership;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } || principal.FindFirstValue("sub") is not { } subject)
        {
            return principal;
        }

        if (!_looked)
        {
            _membership = await db.TenantMemberships.AsNoTracking().SingleOrDefaultAsync(m => m.Subject == subject);
            _looked = true;
        }

        var transformed = principal.Clone();
        var identity = (ClaimsIdentity)transformed.Identity!;
        foreach (var claim in identity.FindAll(Tenancy.TenantClaim).ToList())
        {
            identity.RemoveClaim(claim); // never trust a tenant from the token
        }

        if (_membership is not null)
        {
            identity.AddClaim(new Claim(Tenancy.TenantClaim, _membership.TenantId.ToString()));
        }

        return transformed;
    }
}
