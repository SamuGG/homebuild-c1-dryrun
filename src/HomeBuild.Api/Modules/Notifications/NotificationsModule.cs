using System.Collections.Concurrent;
using HomeBuild.Api.Http;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Notifications;

/// <summary>
/// Notifications (stub until C10's emails): writes the buyer a message when one of their home's milestones completes.
/// Kept in memory; the message holds only what a buyer may see (no site notes, subcontractors or defects).
/// </summary>
public static class NotificationsModule
{
    public const string Name = "Notifications";

    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        // Singleton: the in-memory outbox outlives requests (a stub for C10's emails).
        services.AddSingleton<BuyerMessages>();
        // Scoped: uses Sales' scoped published reads.
        services.AddScoped<IHandleDomainEvent<MilestoneCompleted>, TellBuyerAboutMilestone>();
        // C9 Lab 7: My Home reads what the notifications Lambda wrote to S3. Singleton: one SDK client, created on first use.
        services.AddOptions<NotificationsOptions>().BindConfiguration(NotificationsOptions.Section);
        services.AddSingleton<INotificationReader, S3NotificationReader>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapVersionedGroup().WithTags(Name).MapMyNotifications();
        return endpoints;
    }
}

internal sealed record BuyerMessage(ReservationId ReservationId, UnitId UnitId, Milestone Milestone, string Text);

internal sealed class BuyerMessages
{
    private readonly ConcurrentDictionary<(UnitId, Milestone), BuyerMessage> messages = new();

    /// <summary>One message per unit and milestone: a repeated event sends nothing new.</summary>
    public bool Write(BuyerMessage message) => messages.TryAdd((message.UnitId, message.Milestone), message);

    public IReadOnlyList<BuyerMessage> For(UnitId unitId) => [.. messages.Values.Where(m => m.UnitId == unitId)];
}

internal sealed class TellBuyerAboutMilestone(IUnitAvailability sales, BuyerMessages messages) : IHandleDomainEvent<MilestoneCompleted>
{
    public async Task HandleAsync(MilestoneCompleted domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (await sales.LatestReservationAsync(domainEvent.UnitId, cancellationToken) is { Status: "held" or "confirmed" } reservation)
        {
            messages.Write(new BuyerMessage(reservation.ReservationId, domainEvent.UnitId, domainEvent.Milestone,
                $"Good news: your home has reached a new stage ({domainEvent.Milestone}) on {domainEvent.CompletedAt:d MMMM yyyy}."));
        }
    }
}
