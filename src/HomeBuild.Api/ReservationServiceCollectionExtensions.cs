using HomeBuild.Domain;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api;

public static class ReservationServiceCollectionExtensions
{
    public static IServiceCollection AddReservationPolicy(this IServiceCollection services)
    {
        // Options are singletons inside the framework (IOptions<T>); IOptionsSnapshot<T> is scoped.
        services.AddOptions<ReservationOptions>()
            .BindConfiguration(ReservationOptions.Section)
            .ValidateOnStart();
        // Singleton: the generated validator is stateless.
        services.AddSingleton<IValidateOptions<ReservationOptions>, ValidateReservationOptions>();
        // Singleton: built once from validated IOptions<T>; settings do not change while the host runs.
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ReservationOptions>>().Value;
            return new ReservationPolicy(o.HoldPeriod, new Money(o.MinimumDeposit, o.Currency));
        });
        // Hosted services are singletons; PolicyStartupLogger depends only on singletons.
        services.AddHostedService<PolicyStartupLogger>();
        return services;
    }
}
