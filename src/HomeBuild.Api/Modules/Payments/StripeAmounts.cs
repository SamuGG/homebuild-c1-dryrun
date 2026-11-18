using System.Collections.Frozen;
using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Payments;

/// <summary>
/// The one place that turns <see cref="Money"/> into the integer Stripe expects (C8 Lab 1). Stripe takes amounts in the
/// currency's minor unit, and not every currency has two decimals (https://docs.stripe.com/currencies, read 18 Nov 2026).
/// An amount Stripe cannot represent exactly is refused, never rounded: rounding money silently charges the wrong sum.
/// </summary>
internal static class StripeAmounts
{
    /// <summary>Zero-decimal: the amount is the integer itself (500 JPY is 500).</summary>
    private static readonly FrozenSet<string> ZeroDecimal = FrozenSet.Create(
        StringComparer.Ordinal, "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "VND", "VUV", "XAF", "XOF", "XPF");

    /// <summary>
    /// Zero-decimal currencies Stripe still takes as two-decimal, with the decimals always 00 (5 ISK is 500): no fractions.
    /// </summary>
    private static readonly FrozenSet<string> WholeButTwoDecimal = FrozenSet.Create(StringComparer.Ordinal, "ISK", "UGX");

    /// <summary>Three-decimal: times 1000, and Stripe requires the last digit to be 0 (1.230 KWD is 1230).</summary>
    private static readonly FrozenSet<string> ThreeDecimal = FrozenSet.Create(StringComparer.Ordinal, "BHD", "JOD", "KWD", "OMR", "TND");

    /// <summary>Card payments take at most 12 digits in minor units.</summary>
    internal const long Maximum = 999_999_999_999;

    public static long ToMinorUnits(Money money)
    {
        ArgumentNullException.ThrowIfNull(money);
        var currency = money.Currency;
        var (factor, mustBeWhole, lastDigitZero) =
            ZeroDecimal.Contains(currency) ? (1m, true, false)
            : WholeButTwoDecimal.Contains(currency) ? (100m, true, false)
            : ThreeDecimal.Contains(currency) ? (1000m, false, true)
            : (100m, false, false);

        if (mustBeWhole && decimal.Truncate(money.Amount) != money.Amount)
        {
            throw new ArgumentException($"{money.Amount} {currency}: Stripe charges whole {currency} only.", nameof(money));
        }

        var minor = money.Amount * factor;
        if (decimal.Truncate(minor) != minor || (lastDigitZero && minor % 10 != 0))
        {
            throw new ArgumentException($"{money.Amount} {currency} is not a whole number of {currency}'s minor unit for Stripe.", nameof(money));
        }

        if (minor > Maximum)
        {
            throw new ArgumentException($"{money.Amount} {currency} exceeds Stripe's 12-digit card limit.", nameof(money));
        }

        return (long)minor;
    }

    /// <summary>Stripe's currency codes are lowercase.</summary>
    public static string Currency(Money money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return money.Currency.ToLowerInvariant();
    }
}
