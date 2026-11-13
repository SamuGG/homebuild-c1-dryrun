using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.v3;

namespace HomeBuild.Api.Tests;

/// <summary>
/// Signs the test's requests in as a user named by an attribute on the test method or class, without a token
/// (C6 Lab 3). Tests without an attribute keep the C4 test issuer, so real JWT validation is still exercised.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public abstract class TestUserAttribute : Attribute
{
    /// <summary>"A" (the demo tenant, C2's fixed data) or "B" (the other tenant).</summary>
    public string Tenant { get; set; } = "A";

    internal abstract (Guid Subject, string[] Groups) User { get; }
}

/// <summary>A buyer with a membership in <see cref="TestUserAttribute.Tenant"/>.</summary>
public sealed class AsBuyerAttribute : TestUserAttribute
{
    /// <summary>Which tenant-A buyer: "default", "owner", "other" or "third" (tenant B has one buyer).</summary>
    public string Buyer { get; set; } = "default";

    internal override (Guid Subject, string[] Groups) User => (Tenant, Buyer) switch
    {
        ("B", _) => (TestTokens.Users.OtherTenantBuyer, ["buyer"]),
        (_, "owner") => (TestTokens.Users.Owner, ["buyer"]),
        (_, "other") => (TestTokens.Users.OtherBuyer, ["buyer"]),
        (_, "third") => (TestTokens.Users.ThirdBuyer, ["buyer"]),
        _ => (TestTokens.DefaultBuyer, ["buyer"]),
    };
}

/// <summary>A staff member of the demo tenant in one role: admin, sales-agent, site-manager, finance or aftercare.</summary>
public sealed class AsStaffAttribute(string role) : TestUserAttribute
{
    public string Role { get; } = role;

    internal override (Guid Subject, string[] Groups) User => (TestTokens.Users.Staff(Role), [Role]);
}

internal static class FakeAuthentication
{
    public const string Scheme = "Fake";
    public const string Header = "X-Test-User";
    private const string Selector = "FakeOrBearer";

    /// <summary>The user the current test asked for with an attribute (method first, then class), if any.</summary>
    public static TestUserAttribute? Requested() =>
        TestContext.Current.TestMethod is IXunitTestMethod method
            ? method.Method.GetCustomAttribute<TestUserAttribute>() ?? method.TestClass.Class.GetCustomAttribute<TestUserAttribute>()
            : null;

    public static string HeaderValue(TestUserAttribute user) => $"{user.User.Subject}|{string.Join(',', user.User.Groups)}";

    /// <summary>Adds the fake scheme and makes it the default whenever a request carries <see cref="Header"/>.</summary>
    public static IServiceCollection AddFakeAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, _ => { })
            .AddPolicyScheme(Selector, null, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(Header) ? Scheme : JwtBearerDefaults.AuthenticationScheme);
        services.PostConfigure<AuthenticationOptions>(options => options.DefaultScheme = Selector);
        return services;
    }

    /// <summary>Issues the claims a Cognito access token carries (sub, token_use, client_id, cognito:groups).</summary>
    private sealed class Handler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers[Header].ToString() is not { Length: > 0 } value || value.Split('|') is not [var subject, var groups])
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new("sub", subject), new("token_use", "access"), new("client_id", "fake-test-auth") };
            claims.AddRange(groups.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(g => new Claim("cognito:groups", g)));
            var identity = new ClaimsIdentity(claims, FakeAuthentication.Scheme, "sub", "cognito:groups");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), FakeAuthentication.Scheme)));
        }
    }
}
