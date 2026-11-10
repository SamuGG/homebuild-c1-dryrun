using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>Picks the keyed deposit strategy that a development uses.</summary>
internal sealed class DepositStrategySelector(
    [FromKeyedServices(DepositStrategyKeys.Fixed)] IDepositStrategy fixedStrategy,
    [FromKeyedServices(DepositStrategyKeys.Percentage)] IDepositStrategy percentageStrategy)
{
    public IDepositStrategy For(string key) => key switch
    {
        DepositStrategyKeys.Fixed => fixedStrategy,
        DepositStrategyKeys.Percentage => percentageStrategy,
        _ => throw new InvalidOperationException($"Unknown deposit strategy '{key}'."),
    };

    public ReservationPolicy PolicyFor(Unit unit, string key, ReservationPolicy policy) =>
        policy with { MinimumDeposit = For(key).MinimumFor(unit, policy) };
}
