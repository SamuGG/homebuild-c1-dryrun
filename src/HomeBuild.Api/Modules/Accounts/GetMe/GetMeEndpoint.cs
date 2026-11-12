using System.Security.Claims;
using HomeBuild.Api.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Accounts.GetMe;

internal sealed record MeResponse(string Subject, string ClientId, IReadOnlyList<string> Roles, Guid? TenantId);

internal static class GetMeEndpoint
{
    public static void MapGetMe(this RouteGroupBuilder group) =>
        group.MapGet("/me", Handle)
            .WithName("GetMe")
            .WithSummary("The caller's identity as the API sees it: subject, client, roles and tenant")
            .WithMetadata(new TenantNotRequiredAttribute());

    private static Ok<MeResponse> Handle(ClaimsPrincipal user) => TypedResults.Ok(new MeResponse(
        user.FindFirstValue("sub") ?? "",
        user.FindFirstValue("client_id") ?? "",
        [.. user.FindAll(Authentication.GroupsClaim).Select(c => c.Value).Order(StringComparer.Ordinal)],
        Guid.TryParse(user.FindFirstValue(Tenancy.TenantClaim), out var tenant) ? tenant : null));
}
