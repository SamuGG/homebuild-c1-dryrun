using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HomeBuild.Api.Tests;

/// <summary>
/// A local test issuer, like dotnet user-jwts: tokens with Cognito's access-token claims (token_use, client_id,
/// cognito:groups as an array, sub), signed with a key generated per test run. It proves HomeBuild's validation
/// logic, not Cognito's configuration.
/// </summary>
public static class TestTokens
{
    public const string Issuer = "https://test-issuer.homebuild.local";
    public const string ClientId = "homebuild-test-client";
    public const string OtherClientId = "some-other-app-client";

    /// <summary>The default test user's Cognito sub (a GUID, like Cognito's): a buyer in the demo tenant.</summary>
    public static readonly Guid DefaultBuyer = Guid.Parse("0198a000-0000-7000-9000-00000000b001");

    /// <summary>Test users with a membership (seeded by <see cref="PostgresFixture"/>), by name.</summary>
    public static class Users
    {
        public static readonly Guid Owner = Guid.Parse("0198a000-0000-7000-9000-00000000b0a1");
        public static readonly Guid OtherBuyer = Guid.Parse("0198a000-0000-7000-9000-00000000b0b2");
        public static readonly Guid ThirdBuyer = Guid.Parse("0198a000-0000-7000-9000-00000000b0c3");
        /// <summary>A buyer whose membership is in <see cref="TestTenants.Other"/>.</summary>
        public static readonly Guid OtherTenantBuyer = Guid.Parse("0198a000-0000-7000-9000-00000000bb01");
        /// <summary>A signed-in user with no membership at all.</summary>
        public static readonly Guid NoMembership = Guid.Parse("0198a000-0000-7000-9000-00000000dead");

        public static Guid Staff(string role) => role switch
        {
            "admin" => Guid.Parse("0198a000-0000-7000-9000-00000000c001"),
            "sales-agent" => Guid.Parse("0198a000-0000-7000-9000-00000000c002"),
            "site-manager" => Guid.Parse("0198a000-0000-7000-9000-00000000c003"),
            "finance" => Guid.Parse("0198a000-0000-7000-9000-00000000c004"),
            "aftercare" => Guid.Parse("0198a000-0000-7000-9000-00000000c005"),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        /// <summary>Every member: (sub, tenant, roles).</summary>
        public static IEnumerable<(Guid Subject, Guid Tenant, string[] Roles)> Memberships()
        {
            yield return (DefaultBuyer, TestTenants.Demo, ["buyer"]);
            yield return (Owner, TestTenants.Demo, ["buyer"]);
            yield return (OtherBuyer, TestTenants.Demo, ["buyer"]);
            yield return (ThirdBuyer, TestTenants.Demo, ["buyer"]);
            yield return (OtherTenantBuyer, TestTenants.Other, ["buyer"]);
            foreach (var role in new[] { "admin", "sales-agent", "site-manager", "finance", "aftercare" })
            {
                yield return (Staff(role), TestTenants.Demo, [role]);
            }
        }
    }

    public static readonly string SigningKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Access(string subject, params string[] groups) => Create(subject, groups);

    public static string Create(
        string subject,
        string[] groups,
        string tokenUse = "access",
        string? clientId = ClientId,
        DateTime? expires = null,
        string? audience = null)
    {
        var claims = new Dictionary<string, object> { ["token_use"] = tokenUse };
        if (clientId is not null)
        {
            claims["client_id"] = clientId;
        }

        if (groups.Length > 0)
        {
            claims["cognito:groups"] = groups; // Cognito sends a JSON array
        }

        if (audience is not null)
        {
            claims["aud"] = audience; // ID tokens have aud = client id
        }

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(30);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            Claims = claims,
            IssuedAt = expiry.AddHours(-1),
            NotBefore = expiry.AddHours(-1),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(SigningKeyBase64)), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>The API configuration that trusts this issuer (the same keys dotnet user-jwts writes).</summary>
    public static IEnumerable<KeyValuePair<string, string>> Settings() =>
    [
        new("Authentication:Schemes:Bearer:ValidIssuer", Issuer),
        new("Authentication:Schemes:Bearer:SigningKeys:0:Issuer", Issuer),
        new("Authentication:Schemes:Bearer:SigningKeys:0:Value", SigningKeyBase64),
        new("Tokens:AllowedClientIds:0", ClientId),
    ];
}
