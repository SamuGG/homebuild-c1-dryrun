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
