using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Accounts.GetMe;

namespace HomeBuild.Api.Modules.Accounts;

/// <summary>The signed-in user's view of themselves. C4 Lab 5 adds tenant membership here.</summary>
public static class AccountsModule
{
    public const string Name = "Accounts";

    public static IEndpointRouteBuilder MapAccountsModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name);
        group.MapGetMe();
        return endpoints;
    }
}
