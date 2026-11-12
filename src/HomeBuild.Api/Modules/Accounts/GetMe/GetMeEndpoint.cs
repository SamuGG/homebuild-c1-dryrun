using System.Security.Claims;
using HomeBuild.Api.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HomeBuild.Api.Modules.Accounts.GetMe;

internal sealed record MeResponse(string Subject, string ClientId, IReadOnlyList<string> Roles);

internal static class GetMeEndpoint
{
    public static void MapGetMe(this RouteGroupBuilder group) =>
        group.MapGet("/me", Handle)
            .WithName("GetMe")
            .WithSummary("The caller's identity as the API sees it: subject, client and roles");

    private static Ok<MeResponse> Handle(ClaimsPrincipal user) => TypedResults.Ok(new MeResponse(
        user.FindFirstValue("sub") ?? "",
        user.FindFirstValue("client_id") ?? "",
        [.. user.FindAll(Authentication.GroupsClaim).Select(c => c.Value).Order(StringComparer.Ordinal)]));
}
