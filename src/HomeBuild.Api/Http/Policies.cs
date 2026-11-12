using System.Security.Claims;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace HomeBuild.Api.Http;

/// <summary>HomeBuild's roles: the Cognito groups created by the Identity stack.</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string SalesAgent = "sales-agent";
    public const string SiteManager = "site-manager";
    public const string Finance = "finance";
    public const string Aftercare = "aftercare";
    public const string Buyer = "buyer";
}

/// <summary>Named role policies (who may attempt an action at all). Ownership is checked per object below.</summary>
public static class Policies
{
    public const string CanManageCatalogue = nameof(CanManageCatalogue);
    public const string CanReserve = nameof(CanReserve);
    public const string CanReserveForOthers = nameof(CanReserveForOthers);
    public const string CanReadReservations = nameof(CanReadReservations);
    public const string CanCancelReservations = nameof(CanCancelReservations);
    public const string CanReadPayments = nameof(CanReadPayments);
    public const string CanViewBuildBoard = nameof(CanViewBuildBoard);
    public const string CanReadOwnProgress = nameof(CanReadOwnProgress);

    public static IServiceCollection AddHomeBuildPolicies(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(CanManageCatalogue, p => p.RequireRole(Roles.Admin))
            .AddPolicy(CanReserve, p => p.RequireRole(Roles.Buyer, Roles.SalesAgent))
            .AddPolicy(CanReserveForOthers, p => p.RequireRole(Roles.SalesAgent))
            .AddPolicy(CanReadReservations, p => p.RequireRole(Roles.Buyer, Roles.SalesAgent, Roles.Finance, Roles.Admin))
            .AddPolicy(CanCancelReservations, p => p.RequireRole(Roles.Buyer, Roles.SalesAgent, Roles.Admin))
            .AddPolicy(CanReadPayments, p => p.RequireRole(Roles.Buyer, Roles.Finance, Roles.Admin))
            .AddPolicy(CanViewBuildBoard, p => p.RequireRole(Roles.SiteManager, Roles.Admin))
            .AddPolicy(CanReadOwnProgress, p => p.RequireRole(Roles.Buyer));
        // Singleton: the handler is stateless.
        services.AddSingleton<IAuthorizationHandler, ReservationAccessHandler>();
        return services;
    }

    /// <summary>The caller as a buyer: their Cognito sub, which is a GUID. Null if it is not one.</summary>
    public static BuyerId? CallerBuyerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? new BuyerId(id) : null;
}

/// <summary>What a caller wants to do with one reservation.</summary>
public static class ReservationOperations
{
    public static readonly OperationAuthorizationRequirement Read = new() { Name = nameof(Read) };
    public static readonly OperationAuthorizationRequirement Cancel = new() { Name = nameof(Cancel) };
    public static readonly OperationAuthorizationRequirement ReadPayments = new() { Name = nameof(ReadPayments) };
}

/// <summary>
/// Resource-based authorisation: staff roles may act on any reservation of their tenant; a buyer only on their own.
/// The endpoint's role policy has already rejected roles that may not attempt the operation at all.
/// </summary>
internal sealed class ReservationAccessHandler : AuthorizationHandler<OperationAuthorizationRequirement, Reservation>
{
    private static readonly Dictionary<string, string[]> StaffRoles = new()
    {
        [nameof(ReservationOperations.Read)] = [Roles.SalesAgent, Roles.Finance, Roles.Admin],
        [nameof(ReservationOperations.Cancel)] = [Roles.SalesAgent, Roles.Admin],
        [nameof(ReservationOperations.ReadPayments)] = [Roles.Finance, Roles.Admin],
    };

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement, Reservation resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(resource);
        var staff = StaffRoles.GetValueOrDefault(requirement.Name, []);
        if (staff.Any(context.User.IsInRole)
            || (context.User.IsInRole(Roles.Buyer) && context.User.CallerBuyerId() == resource.BuyerId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
