using System.Security.Claims;
using System.Security.Cryptography;
using HomeBuild.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Api.Http.Idempotency;

/// <summary>Marks a POST that requires an Idempotency-Key header (C8 Lab 3).</summary>
internal sealed class RequiresIdempotencyKey;

public static class IdempotencyExtensions
{
    public const string Header = "Idempotency-Key";

    /// <summary>The endpoint needs an Idempotency-Key (a UUID from the client); repeats replay the first response.</summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequiresIdempotencyKey());

    public static IServiceCollection AddIdempotency(this IServiceCollection services) =>
        services.AddDbContext<IdempotencyDbContext>((sp, options) => IdempotencyDbContext.Configure(options, HomeBuildDatabase.ConnectionString(sp)).AddInterceptors(sp.GetRequiredService<OpenTransactions>()));
}

/// <summary>
/// C8 Lab 3: Idempotency-Key for the marked POSTs. The key is claimed first (insert, unique on caller and key) and
/// completed with the response afterwards:
/// <list type="bullet">
/// <item>same key, same fingerprint (method, path, body hash), completed: the stored response is replayed;</item>
/// <item>same key, different fingerprint: 422;</item>
/// <item>same key still in progress (a concurrent request, or one that crashed mid-way): 409;</item>
/// <item>a 5xx or an exception releases the key, so the client may retry with it; keys expire after 24 hours.</item>
/// </list>
/// The claim commits before the work, not inside the module's transaction (the table is shared by Sales and Payments),
/// so a crash between the work and the stored response leaves the key in progress (409) rather than re-running the
/// work; the business change keeps its own guard (one active reservation per unit; Stripe's own idempotency key).
/// </summary>
internal sealed partial class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public async Task InvokeAsync(HttpContext context, IdempotencyDbContext db, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(db);
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresIdempotencyKey>() is null)
        {
            await next(context);
            return;
        }

        var ct = context.RequestAborted;
        if (!Guid.TryParse(context.Request.Headers[IdempotencyExtensions.Header], out var key) || key == Guid.Empty)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "idempotency-key-required", "Idempotency-Key required",
                $"This request needs an {IdempotencyExtensions.Header} header: a UUID the client generates once per action and sends again on every retry.");
            return;
        }

        // Scoped to the caller: another user's request with the same key is a different request.
        var caller = context.User.FindFirstValue("sub") ?? throw new InvalidOperationException("Idempotent endpoints require a signed-in caller.");
        string fingerprint;
        try
        {
            fingerprint = await FingerprintAsync(context.Request, ct);
        }
        catch (BadHttpRequestException e)
        {
            // Read here before the endpoint does: Kestrel's body limit (64 KB, C4 red team) still answers 413.
            await WriteProblemAsync(context, e.StatusCode, "bad-request-body", "Request body rejected", e.Message);
            return;
        }

        var now = time.GetUtcNow();

        var claimed = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO idempotency.idempotency_keys (caller, key, fingerprint, status, created_at)
            VALUES ({caller}, {key}, {fingerprint}, {IdempotencyRecord.InProgress}, {now})
            ON CONFLICT (caller, key) DO UPDATE
              SET fingerprint = EXCLUDED.fingerprint, status = EXCLUDED.status, created_at = EXCLUDED.created_at,
                  response_status = NULL, response_content_type = NULL, response_location = NULL, response_body = NULL, completed_at = NULL
              WHERE idempotency.idempotency_keys.created_at < {now - Lifetime}
            """, ct);
        if (claimed == 0)
        {
            await AnswerRepeatAsync(context, db, caller, key, fingerprint, ct);
            return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            await ReleaseAsync(db, caller, key);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }

        if (context.Response.StatusCode >= 500)
        {
            await ReleaseAsync(db, caller, key);
        }
        else
        {
            var body = buffer.ToArray();
            string? location = context.Response.Headers.Location;
            await db.Keys.Where(k => k.Caller == caller && k.Key == key).ExecuteUpdateAsync(set => set
                .SetProperty(k => k.Status, IdempotencyRecord.Completed)
                .SetProperty(k => k.ResponseStatus, context.Response.StatusCode)
                .SetProperty(k => k.ResponseContentType, context.Response.ContentType)
                .SetProperty(k => k.ResponseLocation, location)
                .SetProperty(k => k.ResponseBody, body)
                .SetProperty(k => k.CompletedAt, time.GetUtcNow()), CancellationToken.None);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, ct);
    }

    private async Task AnswerRepeatAsync(HttpContext context, IdempotencyDbContext db, string caller, Guid key, string fingerprint, CancellationToken ct)
    {
        var stored = await db.Keys.AsNoTracking().SingleAsync(k => k.Caller == caller && k.Key == key, ct);
        if (stored.Fingerprint != fingerprint)
        {
            KeyReused(logger, key);
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "Idempotency-Key reused",
                "This key was used for a different request. Generate a new key for a new action.");
            return;
        }

        if (stored.Status != IdempotencyRecord.Completed)
        {
            context.Response.Headers.RetryAfter = "1";
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "idempotency-in-progress", "Request still in progress",
                "The first request with this key has not finished. Retry with the same key shortly.");
            return;
        }

        Replayed(logger, key, stored.ResponseStatus!.Value);
        context.Response.StatusCode = stored.ResponseStatus!.Value;
        context.Response.Headers["Idempotency-Replayed"] = "true";
        if (stored.ResponseContentType is { } contentType)
        {
            context.Response.ContentType = contentType;
        }

        if (stored.ResponseLocation is { } location)
        {
            context.Response.Headers.Location = location;
        }

        if (stored.ResponseBody is { Length: > 0 } body)
        {
            await context.Response.Body.WriteAsync(body, ct);
        }
    }

    // CancellationToken.None: the key must be released even when the client has gone.
    private static Task<int> ReleaseAsync(IdempotencyDbContext db, string caller, Guid key) =>
        db.Keys.Where(k => k.Caller == caller && k.Key == key && k.Status == IdempotencyRecord.InProgress).ExecuteDeleteAsync(CancellationToken.None);

    private static async Task<string> FingerprintAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes($"{request.Method} {request.Path}\n"));
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            sha.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string type, string title, string detail)
    {
        context.Response.StatusCode = status;
        await context.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = Problems.Business(context, type, title, status, detail) });
    }

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Idempotency-Key {Key} replayed the stored {Status} response")]
    private static partial void Replayed(ILogger logger, Guid key, int status);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "Idempotency-Key {Key} reused for a different request")]
    private static partial void KeyReused(ILogger logger, Guid key);
}
