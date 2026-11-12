using HomeBuild.Api.Data;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Accounts.GetMe;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Accounts;

/// <summary>The signed-in user's view of themselves. C4 Lab 5 adds tenant membership here.</summary>
public static class AccountsModule
{
    public const string Name = "Accounts";

    public static IServiceCollection AddAccountsModule(this IServiceCollection services)
    {
        // Scoped (AddDbContext's default).
        services.AddDbContext<AccountsDbContext>((sp, options) => Configure(options, HomeBuildDatabase.ConnectionString(sp)));
        // Scoped: the transformation caches the membership for one request only (C16 revisits caching).
        services.AddScoped<IClaimsTransformation, TenantClaimsTransformation>();
        return services;
    }

    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseHomeBuildDatabase(connectionString, AccountsDbContext.Schema)
            .UseSeeding((db, _) => AccountsDbContext.SeedAsync(db, CancellationToken.None).GetAwaiter().GetResult())
            .UseAsyncSeeding((db, _, ct) => AccountsDbContext.SeedAsync(db, ct));

    internal static AccountsDbContext CreateDbContext(string connectionString) =>
        new((DbContextOptions<AccountsDbContext>)Configure(new DbContextOptionsBuilder<AccountsDbContext>(), connectionString).Options);

    public static IEndpointRouteBuilder MapAccountsModule(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapVersionedGroup().WithTags(Name);
        group.MapGetMe();
        return endpoints;
    }
}
