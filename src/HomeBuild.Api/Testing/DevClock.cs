#if DEBUG
using HomeBuild.Api.Http;

namespace HomeBuild.Api.Testing;

/// <summary>
/// C10 Lab 7, test-only (Debug builds, Development environment): the system clock plus an offset an end-to-end test can
/// move forward, so time rules (the defects-period cut-off) can be reached without waiting. Compiled out of Release
/// builds; <c>Test_only_endpoints_are_absent_from_release_build</c> checks it.
/// </summary>
internal sealed class DevClock : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
}

internal sealed record AdvanceClockRequest(TimeSpan By);

internal static class DevClockEndpoints
{
    /// <summary>Replaces TimeProvider with the dev clock (call after every module registered TimeProvider.System).</summary>
    public static void AddDevClock(this IServiceCollection services)
    {
        services.AddSingleton<DevClock>();
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<DevClock>());
    }

    /// <summary>POST /dev/clock {"by":"731.00:00:00"} moves the API's clock forward; DELETE /dev/clock puts it back.</summary>
    public static void MapDevClock(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/dev/clock").WithTags(HomeBuild.Api.Modules.Aftercare.AftercareModule.Name).AllowAnonymous().WithMetadata(new TenantNotRequiredAttribute()).ExcludeFromDescription();
        group.MapGet("", (DevClock clock) => TypedResults.Ok(new { now = clock.GetUtcNow(), offset = clock.Offset }));
        group.MapPost("", (AdvanceClockRequest request, DevClock clock) =>
        {
            clock.Advance(request.By < TimeSpan.Zero ? TimeSpan.Zero : request.By); // forward only
            return TypedResults.Ok(new { now = clock.GetUtcNow(), offset = clock.Offset });
        });
        group.MapDelete("", (DevClock clock) =>
        {
            clock.Reset();
            return TypedResults.Ok(new { now = clock.GetUtcNow(), offset = clock.Offset });
        });
    }
}
#endif
