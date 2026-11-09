using HomeBuild.Domain;
using Microsoft.Extensions.Options;

namespace HomeBuild.Api;

public static class ReservationServiceCollectionExtensions
{
    public static IServiceCollection AddReservationPolicy(this IServiceCollection services)
    {
        services.AddOptions<ReservationOptions>()
            .BindConfiguration(ReservationOptions.Section)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReservationOptions>, ValidateReservationOptions>();
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ReservationOptions>>().Value;
            return new ReservationPolicy(o.HoldPeriod, new Money(o.MinimumDeposit, o.Currency));
        });
        services.AddHostedService<PolicyStartupLogger>();
        return services;
    }
}
