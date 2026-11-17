using System.Collections.Concurrent;
using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Extras;

/// <summary>
/// Extras (stub until C10 builds the feature): records that a unit's extras unlocked when its frame completes, so the
/// buyer can choose finishes. Kept in memory: C10 replaces it with real storage.
/// </summary>
public static class ExtrasModule
{
    public const string Name = "Extras";

    /// <summary>The milestone that unlocks extras: once the frame is up, buyers choose kitchens and finishes.</summary>
    public const Milestone UnlockedBy = Milestone.Frame;

    public static IServiceCollection AddExtrasModule(this IServiceCollection services)
    {
        // Singleton: the in-memory ledger outlives requests (a stub for C10's table).
        services.AddSingleton<ExtrasLedger>();
        // Scoped: resolved by the dispatcher in the request's scope.
        services.AddScoped<IHandleDomainEvent<MilestoneCompleted>, UnlockExtrasOnFrame>();
        return services;
    }

    public static IEndpointRouteBuilder MapExtrasModule(this IEndpointRouteBuilder endpoints) => endpoints;
}

internal sealed record ExtrasUnlocked(UnitId UnitId, DateOnly UnlockedOn);

internal sealed class ExtrasLedger
{
    private readonly ConcurrentDictionary<UnitId, ExtrasUnlocked> unlocked = new();

    /// <summary>Records the unlock once; a second record for the same unit changes nothing.</summary>
    public bool Record(ExtrasUnlocked entry) => unlocked.TryAdd(entry.UnitId, entry);

    public ExtrasUnlocked? For(UnitId unitId) => unlocked.GetValueOrDefault(unitId);
}

internal sealed class UnlockExtrasOnFrame(ExtrasLedger ledger) : IHandleDomainEvent<MilestoneCompleted>
{
    public Task HandleAsync(MilestoneCompleted domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Milestone == ExtrasModule.UnlockedBy)
        {
            ledger.Record(new ExtrasUnlocked(domainEvent.UnitId, domainEvent.CompletedAt));
        }

        return Task.CompletedTask;
    }
}
