using HomeBuild.Domain;

namespace HomeBuild.Api.Data;

/// <summary>The small fixed data set the tests and the Bruno collection address by ID (C2 Lab 1).</summary>
internal static class SeedData
{
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

    private static Unit NewUnit(DevelopmentId development, int developmentNumber, int plot, decimal price) => new(
        new UnitId(Guid.Parse($"0198a000-0000-7000-8{developmentNumber:000}-{plot:000000000000}")),
        development,
        $"P{plot}",
        new Money(price, "GBP"),
        new UnitSpecification(plot + 1, plot, plot % 2 == 1, ["oak floors", plot == 3 ? "quartz worktops" : "laminate worktops"]));
}
