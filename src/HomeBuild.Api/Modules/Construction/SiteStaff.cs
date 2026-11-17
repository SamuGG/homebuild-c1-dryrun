using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Construction;

/// <summary>A site manager's assignment to a development: only assigned site managers act on its units.</summary>
internal sealed record SiteAssignment(string SiteManager, DevelopmentId DevelopmentId);

/// <summary>One row per milestone change or correction: who, when, what and why (C7 Lab 4).</summary>
internal sealed class MilestoneAudit
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required UnitId UnitId { get; init; }

    public required Milestone Milestone { get; init; }

    /// <summary>Completed or Corrected.</summary>
    public required string Action { get; init; }

    public required string By { get; init; }

    public required DateTimeOffset At { get; init; }

    public DateOnly? From { get; init; }

    public required DateOnly To { get; init; }

    public string? Reason { get; init; }
}

/// <summary>What a site manager wants to do on a development.</summary>
internal static class SiteOperations
{
    public static readonly OperationAuthorizationRequirement CompleteMilestone = new() { Name = nameof(CompleteMilestone) };
    public static readonly OperationAuthorizationRequirement WriteDiary = new() { Name = nameof(WriteDiary) };
}

/// <summary>
/// Resource-based authorisation: the resource is the development the unit belongs to, as the server knows it (from the
/// unit's BuildProgress), never as the request says. Only a site manager assigned to it succeeds.
/// </summary>
internal sealed class SiteAssignmentHandler(ConstructionDbContext db) : AuthorizationHandler<OperationAuthorizationRequirement, DevelopmentId>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement, DevelopmentId resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User.IsInRole(Roles.SiteManager)
            && context.User.FindFirst("sub")?.Value is { } subject
            && await db.SiteAssignments.AnyAsync(a => a.SiteManager == subject && a.DevelopmentId == resource))
        {
            context.Succeed(requirement);
        }
    }
}
