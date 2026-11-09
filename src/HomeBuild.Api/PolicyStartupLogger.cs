using HomeBuild.Domain;

namespace HomeBuild.Api;

internal sealed class PolicyStartupLogger(ReservationPolicy policy, ILogger<PolicyStartupLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.PolicyLoaded(policy.HoldPeriod, policy.MinimumDeposit.Amount, policy.MinimumDeposit.Currency);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
