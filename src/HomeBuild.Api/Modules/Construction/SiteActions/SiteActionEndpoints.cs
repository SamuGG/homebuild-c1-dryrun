using System.ComponentModel.DataAnnotations;
using HomeBuild.Api.Http;
using HomeBuild.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Modules.Construction.SiteActions;

/// <summary>Body of a milestone completion. Nothing in it names a development: the server looks that up.</summary>
internal sealed record CompleteMilestoneRequest(DateOnly CompletedOn);

internal sealed record CorrectMilestoneRequest(DateOnly CompletedOn, [property: StringLength(1000)] string? Reason);

internal sealed record WriteDiaryEntryRequest(
    DateOnly WrittenOn, [property: Required, StringLength(4000, MinimumLength = 1)] string Text, [property: StringLength(100)] string? Weather, string[]? PhotoKeys);

internal sealed record MilestoneResponse(Guid UnitId, string Milestone, string Status, DateOnly? CompletedOn, string? CompletedBy);

internal sealed record DiaryEntryResponse(Guid Id, Guid UnitId, DateOnly WrittenOn, string Text, string Weather, string WrittenBy, IReadOnlyList<string> PhotoKeys);

internal sealed record DiaryPhotoUploadResponse(Uri Url, IReadOnlyDictionary<string, string> Fields, string Key, long MaxBytes, string ContentType, DateTimeOffset ExpiresAt);

/// <summary>
/// C7 Lab 4: site staff actions. The role policy says who may try (site managers; admins for corrections); the
/// assignment check says on which development, using the development the server holds for the unit.
/// </summary>
internal static class SiteActionEndpoints
{
    public static void MapSiteActions(this RouteGroupBuilder group)
    {
        group.MapPost("/units/{unitId}/milestones/{milestone}/completion", Complete)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("CompleteMilestoneV2")
            .WithSummary("Complete a unit's milestone (a site manager assigned to its development, v2)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(Policies.CanWorkOnSite);

        group.MapPost("/units/{unitId}/milestones/{milestone}/correction", Correct)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("CorrectMilestoneV2")
            .WithSummary("Correct a completed milestone's date, with a reason; audited (admin, v2)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(Policies.CanCorrectMilestones);

        group.MapPost("/units/{unitId}/diary", WriteDiary)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("WriteDiaryEntryV2")
            .WithSummary("Write a site diary entry for a unit (a site manager assigned to its development, v2)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization(Policies.CanWorkOnSite);

        group.MapPost("/units/{unitId}/diary/photos/uploads", StartPhotoUpload)
            .MapToApiVersion(ApiVersions.V2)
            .WithName("StartDiaryPhotoUploadV2")
            .WithSummary("A pre-signed POST for a diary photo (JPEG, up to 10 MB, 5 minutes) straight to S3 under diary/ (v2)")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(Policies.CanWorkOnSite);
    }

    private static async Task<Results<Ok<MilestoneResponse>, NotFound, ProblemHttpResult, UnprocessableEntity<ProblemDetails>, Conflict<ProblemDetails>>> Complete(
        UnitId unitId, string milestone, CompleteMilestoneRequest request, ConstructionDbContext db, IAuthorizationService authorization,
        TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<Milestone>(milestone, ignoreCase: true, out var which)
            || await db.BuildProgress.SingleOrDefaultAsync(p => p.UnitId == unitId, cancellationToken) is not { } progress)
        {
            return TypedResults.NotFound();
        }

        if (await Forbidden(authorization, http, progress.DevelopmentId, SiteOperations.CompleteMilestone) is { } forbidden)
        {
            return forbidden;
        }

        var siteManager = http.User.FindFirst("sub")!.Value;
        switch (progress.Complete(which, siteManager, request.CompletedOn).Value)
        {
            case MilestoneDone done:
                db.MilestoneAudits.Add(new MilestoneAudit
                {
                    UnitId = unitId, Milestone = which, Action = "Completed", By = siteManager, At = time.GetUtcNow(), To = done.On,
                });
                if (!await TrySaveAsync(db, cancellationToken))
                {
                    return TypedResults.Conflict(Problems.Business(http, "milestone-conflict", "Build progress changed", StatusCodes.Status409Conflict,
                        "Another change to this unit's milestones was saved first. Reload and try again."));
                }

                return TypedResults.Ok(Response(progress, which));
            case var refused:
                return TypedResults.UnprocessableEntity(Problems.Business(
                    http, "milestone-rule", "Milestone cannot complete", StatusCodes.Status422UnprocessableEntity, Describe(refused)));
        }
    }

    private static async Task<Results<Ok<MilestoneResponse>, NotFound, ValidationProblem, UnprocessableEntity<ProblemDetails>, Conflict<ProblemDetails>>> Correct(
        UnitId unitId, string milestone, CorrectMilestoneRequest request, ConstructionDbContext db, TimeProvider time, HttpContext http,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<Milestone>(milestone, ignoreCase: true, out var which)
            || await db.BuildProgress.SingleOrDefaultAsync(p => p.UnitId == unitId, cancellationToken) is not { } progress)
        {
            return TypedResults.NotFound();
        }

        switch (progress.CorrectCompletion(which, request.CompletedOn, request.Reason ?? "").Value)
        {
            case ReasonRequired:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["A correction needs a reason."] });
            case CompletionCorrected corrected:
                db.MilestoneAudits.Add(new MilestoneAudit
                {
                    UnitId = unitId, Milestone = which, Action = "Corrected", By = http.User.FindFirst("sub")!.Value, At = time.GetUtcNow(),
                    From = corrected.From, To = corrected.To, Reason = request.Reason!.Trim(),
                });
                if (!await TrySaveAsync(db, cancellationToken))
                {
                    return TypedResults.Conflict(Problems.Business(http, "milestone-conflict", "Build progress changed", StatusCodes.Status409Conflict));
                }

                return TypedResults.Ok(Response(progress, which));
            case var refused:
                return TypedResults.UnprocessableEntity(Problems.Business(
                    http, "milestone-correction", "Milestone cannot be corrected", StatusCodes.Status422UnprocessableEntity, Describe(refused)));
        }
    }

    private static async Task<Results<Created<DiaryEntryResponse>, NotFound, ProblemHttpResult, ValidationProblem>> WriteDiary(
        UnitId unitId, WriteDiaryEntryRequest request, ConstructionDbContext db, IAuthorizationService authorization, ITenantContext tenant,
        HttpContext http, CancellationToken cancellationToken)
    {
        if (await db.BuildProgress.AsNoTracking().Where(p => p.UnitId == unitId).Select(p => (DevelopmentId?)p.DevelopmentId)
                .SingleOrDefaultAsync(cancellationToken) is not { } developmentId)
        {
            return TypedResults.NotFound();
        }

        if (await Forbidden(authorization, http, developmentId, SiteOperations.WriteDiary) is { } forbidden)
        {
            return forbidden;
        }

        var photos = request.PhotoKeys ?? [];
        if (photos.FirstOrDefault(k => !DiaryPhotoStorage.IsOwnKey(k, tenant.TenantId!.Value, unitId)) is { } foreign)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["photoKeys"] = [$"'{foreign}' is not a diary photo of this unit."] });
        }

        var entry = new DiaryEntry(DiaryEntryId.New(), unitId, request.WrittenOn, request.Text.Trim(), request.Weather?.Trim() ?? "",
            http.User.FindFirst("sub")!.Value, photos);
        db.DiaryEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/v2/units/{unitId.Value}/diary/{entry.Id.Value}",
            new DiaryEntryResponse(entry.Id.Value, unitId.Value, entry.WrittenOn, entry.Text, entry.Weather, entry.WrittenBy, entry.PhotoKeys));
    }

    private static async Task<Results<Ok<DiaryPhotoUploadResponse>, NotFound, ProblemHttpResult>> StartPhotoUpload(
        UnitId unitId, ConstructionDbContext db, IAuthorizationService authorization, ITenantContext tenant, DiaryPhotoStorage storage,
        HttpContext http, CancellationToken cancellationToken)
    {
        if (await db.BuildProgress.AsNoTracking().Where(p => p.UnitId == unitId).Select(p => (DevelopmentId?)p.DevelopmentId)
                .SingleOrDefaultAsync(cancellationToken) is not { } developmentId)
        {
            return TypedResults.NotFound();
        }

        if (await Forbidden(authorization, http, developmentId, SiteOperations.WriteDiary) is { } forbidden)
        {
            return forbidden;
        }

        if (!storage.IsConfigured)
        {
            return TypedResults.Problem(Problems.Business(http, "storage-unavailable", "Uploads are not configured", StatusCodes.Status503ServiceUnavailable));
        }

        var (url, fields, key, expires) = await storage.CreateUploadAsync(tenant.TenantId!.Value, unitId);
        return TypedResults.Ok(new DiaryPhotoUploadResponse(url, fields, key, DiaryPhotoStorage.MaxBytes, DiaryPhotoStorage.JpegContentType, expires));
    }

    /// <summary>The assignment check, against the development the server holds for the unit.</summary>
    private static async Task<ProblemHttpResult?> Forbidden(
        IAuthorizationService authorization, HttpContext http, DevelopmentId developmentId, OperationAuthorizationRequirement operation) =>
        (await authorization.AuthorizeAsync(http.User, developmentId, operation)).Succeeded
            ? null
            : TypedResults.Problem(Problems.Business(http, "not-assigned", "Not assigned to this development", StatusCodes.Status403Forbidden,
                "Only a site manager assigned to the unit's development may do this."));

    private static async Task<bool> TrySaveAsync(ConstructionDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static MilestoneResponse Response(BuildProgress progress, Milestone milestone)
    {
        var m = progress[milestone];
        return new MilestoneResponse(progress.UnitId.Value, BuildProgressReader.DisplayName(milestone), BuildProgressReader.BoardStatus(m.Status), m.CompletedOn, m.CompletedBy);
    }

    private static string Describe(object? refused) => refused switch
    {
        AlreadyCompleted a => $"{a.Milestone} was already completed on {a.CompletedOn:yyyy-MM-dd}.",
        OutOfOrder o => $"{o.Milestone} cannot complete before {o.Waiting}.",
        CompletedBeforePrevious c => $"{c.Milestone} cannot complete on {c.On:yyyy-MM-dd}, before {c.Previous} ({c.PreviousOn:yyyy-MM-dd}).",
        EarlierMilestonesOpen e => $"{e.Milestone} needs {string.Join(", ", e.Open)} first.",
        MilestoneNotCompletable n => n.Reason,
        NotCompleted n => $"{n.Milestone} is {n.State}; only a completed milestone can be corrected.",
        CorrectionBreaksOrder b => $"{b.CompletedOn:yyyy-MM-dd} would put {b.Milestone} out of order with its neighbours.",
        _ => "The milestone rules refused this change.",
    };
}
