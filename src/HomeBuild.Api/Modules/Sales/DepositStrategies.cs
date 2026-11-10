using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Sales;

/// <summary>Works out the minimum deposit for a unit.</summary>
internal interface IDepositStrategy
{
    Money MinimumFor(Unit unit, ReservationPolicy policy);
}

internal static class DepositStrategyKeys
{
    public const string Fixed = "fixed";
    public const string Percentage = "percentage";
}

/// <summary>The configured minimum deposit, whatever the price.</summary>
internal sealed class FixedDepositStrategy : IDepositStrategy
{
    public Money MinimumFor(Unit unit, ReservationPolicy policy) => policy.MinimumDeposit;
}

/// <summary>Five per cent of the unit's price.</summary>
internal sealed class PercentageDepositStrategy : IDepositStrategy
{
    private const decimal Rate = 0.05m;

    public Money MinimumFor(Unit unit, ReservationPolicy policy) =>
        new(decimal.Round(unit.Price.Amount * Rate, 2), unit.Price.Currency);
}
