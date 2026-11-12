using System.Globalization;

namespace HomeBuild.Web.Catalogue;

public static class Money
{
    private static readonly CultureInfo UnitedKingdom = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Formats a price for display, for example £325,000 for GBP.</summary>
    public static string Format(decimal amount, string currency) => currency == "GBP"
        ? amount.ToString("C0", UnitedKingdom)
        : $"{amount.ToString("N0", UnitedKingdom)} {currency}";
}
