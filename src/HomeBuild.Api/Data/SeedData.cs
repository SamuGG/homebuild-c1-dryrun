using HomeBuild.Domain;

namespace HomeBuild.Api.Data;

/// <summary>The small fixed data set the tests and the Bruno collection address by ID (C2 Lab 1).</summary>
internal static class SeedData
{
    /// <summary>The tenant that owns C2's fixed data set (both developments).</summary>
    public static readonly Guid DemoTenant = Guid.Parse("0198a000-0000-7000-8000-00000000000a");

    public static readonly DevelopmentId Riverside = new(Guid.Parse("0198a000-0000-7000-8000-000000000001"));
    public static readonly DevelopmentId Hillcrest = new(Guid.Parse("0198a000-0000-7000-8000-000000000002"));

    public static IEnumerable<(DevelopmentId Id, string Name, string Town, string DepositStrategy)> Developments() =>
    [
        (Riverside, "Riverside", "York", "fixed"),
        (Hillcrest, "Hillcrest", "Leeds", "percentage"),
    ];

    public static IEnumerable<Unit> Units()
    {
        for (var plot = 1; plot <= 3; plot++)
        {
            yield return NewUnit(Riverside, 1, plot, 250_000m + (plot * 10_000m));
            yield return NewUnit(Hillcrest, 2, plot, 320_000m + (plot * 10_000m));
        }
    }

    public static (string Name, string Description) TextFor(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var development = unit.DevelopmentId == Riverside ? "Riverside" : "Hillcrest";
        var view = unit.DevelopmentId == Riverside ? "views over the river" : "views across the hills";
        var outdoor = unit.Specification.Balcony ? "a south-facing balcony" : "a private garden";
        return ($"{development} plot {unit.PlotNumber}",
            $"{unit.Specification.Bedrooms}-bedroom home with {outdoor}, {view} and {string.Join(", ", unit.Specification.Finishes)}.");
    }

    private static Unit NewUnit(DevelopmentId development, int developmentNumber, int plot, decimal price) => new(
        new UnitId(Guid.Parse($"0198a000-0000-7000-8{developmentNumber:000}-{plot:000000000000}")),
        development,
        $"P{plot}",
        new Money(price, "GBP"),
        new UnitSpecification(plot + 1, plot, plot % 2 == 1, ["oak floors", plot == 3 ? "quartz worktops" : "laminate worktops"]));
}
