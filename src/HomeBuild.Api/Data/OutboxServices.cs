using Amazon;
using Amazon.SimpleNotificationService;
using HomeBuild.Api.Modules.Construction;
using HomeBuild.Api.Modules.Payments;
using HomeBuild.Api.Modules.Sales;
using HomeBuild.Messaging;

namespace HomeBuild.Api.Data;

internal static class OutboxServices
{
    /// <summary>C9 Lab 2: the SNS publisher and one relay per module outbox (Construction, Payments, Sales).</summary>
    public static IServiceCollection AddOutboxRelays(this IServiceCollection services)
    {
        services.AddOptions<MessagingOptions>().BindConfiguration(MessagingOptions.Section);
        // Singleton: the SDK client is thread-safe; credentials come from the default chain (AWS_PROFILE locally).
        services.AddSingleton<IAmazonSimpleNotificationService>(_ => new AmazonSimpleNotificationServiceClient(RegionEndpoint.EUWest2));
        services.AddScoped<IEventPublisher, SnsEventPublisher>();
        AddRelay<ConstructionDbContext>(services);
        AddRelay<PaymentsDbContext>(services);
        AddRelay<SalesDbContext>(services);
        AddRelay<HomeBuild.Api.Modules.Extras.ExtrasDbContext>(services); // C10 Lab 1: ExtrasUnlocked
        AddRelay<HomeBuild.Api.Modules.Aftercare.AftercareDbContext>(services); // C10 Lab 4: HandoverCompleted
        return services;
    }

    // Singleton (it creates a scope per pass) and also the hosted service, which runs only when a topic is configured.
    private static void AddRelay<TContext>(IServiceCollection services)
        where TContext : Microsoft.EntityFrameworkCore.DbContext
    {
        services.AddSingleton<OutboxRelay<TContext>>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay<TContext>>());
    }
}
