using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Payments.Infrastructure;

/// <summary>Registers the Payments adapters. The host adds an <see cref="IPaymentGateway"/> (Stripe in C8).</summary>
public static class PaymentsInfrastructure
{
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<PaymentsDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IPaymentRecords, EfPaymentRecords>();
        services.AddScoped<DepositPayments>();
        return services;
    }
}
