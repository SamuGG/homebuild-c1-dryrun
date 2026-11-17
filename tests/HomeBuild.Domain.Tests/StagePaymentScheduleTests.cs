using static HomeBuild.Testing.Builders;

namespace HomeBuild.Domain.Tests;

/// <summary>C7 Lab 3: stage payment schedules (Strategy). Percentage schedules always add up to the price, to the penny.</summary>
public class StagePaymentScheduleTests
{
    private static readonly (Milestone, decimal)[] Quarters =
        [(Milestone.Foundations, 25m), (Milestone.Roof, 25m), (Milestone.SecondFix, 25m), (Milestone.PracticalCompletion, 25m)];

    private static readonly (Milestone, decimal)[] Thirds =
        [(Milestone.Frame, 33.33m), (Milestone.Plastering, 33.33m), (Milestone.PracticalCompletion, 33.34m)];

    private static readonly (Milestone, decimal)[] Uneven =
        [(Milestone.Foundations, 10m), (Milestone.Frame, 12.5m), (Milestone.Roof, 17.5m), (Milestone.FirstFix, 15m), (Milestone.UtilitiesConnected, 15m), (Milestone.PracticalCompletion, 30m)];

    private static readonly (Milestone, decimal)[] Sevenths =
        [(Milestone.Foundations, 14.28m), (Milestone.Frame, 14.28m), (Milestone.Roof, 14.28m), (Milestone.FirstFix, 14.29m), (Milestone.Plastering, 14.29m), (Milestone.SecondFix, 14.29m), (Milestone.PracticalCompletion, 14.29m)];

    // (price, schedule, stage amounts expected). Each row names the case it covers.
    public static TheoryData<decimal, (Milestone, decimal)[], decimal[]> PercentageRows =>
    [
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(250_000m, Quarters, [62_500m, 62_500m, 62_500m, 62_500m])
            .WithTestDisplayName("quarters of a round price: no rounding"),
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(250_000.01m, Quarters, [62_500m, 62_500m, 62_500m, 62_500.01m])
            .WithTestDisplayName("quarters of a price with an odd penny: the last stage takes it"),
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(250_000.03m, Quarters, [62_500m, 62_500m, 62_500m, 62_500.03m])
            .WithTestDisplayName("quarters of a price ending in 3p: three rounding pennies, all on the last stage"),
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(100_000m, Thirds, [33_330m, 33_330m, 33_340m])
            .WithTestDisplayName("thirds as 33.33/33.33/33.34 of a round price"),
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(333_333.33m, Thirds, [111_099.99m, 111_099.99m, 111_133.35m])
            .WithTestDisplayName("thirds of a price that rounds in every stage"),
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(199_999.99m, Uneven, [19_999.99m, 24_999.99m, 34_999.99m, 29_999.99m, 29_999.99m, 60_000.04m])
            .WithTestDisplayName("six uneven stages of a price one penny under a round number"),
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(0.07m, Sevenths, [0m, 0m, 0m, 0.01m, 0.01m, 0.01m, 0.04m])
            .WithTestDisplayName("seven stages of 7p: rounding is most of the price"),
        new TheoryDataRow<decimal, (Milestone, decimal)[], decimal[]>(0m, Quarters, [0m, 0m, 0m, 0m])
            .WithTestDisplayName("zero price"),
    ];

    [Theory]
    [MemberData(nameof(PercentageRows))]
    public void Percentage_schedule_sums_to_the_price_including_the_rounding_penny(decimal price, (Milestone, decimal)[] stages, decimal[] expected)
    {
        var plan = new PercentageOfPrice(stages).Plan(Amount(price));

        Assert.Equal(expected, plan.Select(p => p.Amount.Amount));
        Assert.Equal(price, plan.Sum(p => p.Amount.Amount));
        Assert.Equal(stages.Select(s => s.Item1), plan.Select(p => p.Milestone));
    }

    [Fact]
    public void Percentage_schedule_sums_to_the_price_for_ten_thousand_prices()
    {
        var random = new Random(20261117);
        foreach (var stages in new[] { Quarters, Thirds, Uneven, Sevenths })
        {
            var schedule = new PercentageOfPrice(stages);
            for (var i = 0; i < 2_500; i++)
            {
                var price = random.Next(0, 100_000_000) / 100m;
                var plan = schedule.Plan(Amount(price));
                Assert.Equal(price, plan.Sum(p => p.Amount.Amount));
                Assert.All(plan.SkipLast(1), p => Assert.True(p.Amount.Amount <= price * stages.Single(s => s.Item1 == p.Milestone).Item2 / 100m));
            }
        }
    }

    [Theory]
    [InlineData(new[] { 50.0, 49.99 }, "the percentages add up to 99.99")]
    [InlineData(new[] { 60.0, 40.01 }, "the percentages add up to 100.01")]
    [InlineData(new[] { 100.0, 0.0 }, "a stage of 0%")]
    public void Percentage_schedule_rejects_percentages_that_do_not_add_up_to_100(double[] percents, string because)
    {
        var stages = percents.Select((p, i) => ((Milestone)i, (decimal)p));

        Assert.Throws<ArgumentException>(() => new PercentageOfPrice(stages));
        Assert.NotEmpty(because);
    }

    [Fact]
    public void Fixed_stages_put_the_balance_at_practical_completion()
    {
        var schedule = new FixedStages([(Milestone.Foundations, 10_000m), (Milestone.Roof, 15_000m)]);

        var plan = schedule.Plan(Amount(250_000m));

        Assert.Equal(
            [(Milestone.Foundations, 10_000m), (Milestone.Roof, 15_000m), (Milestone.PracticalCompletion, 225_000m)],
            plan.Select(p => (p.Milestone, p.Amount.Amount)));
    }

    [Fact]
    public void Fixed_stages_larger_than_the_price_are_refused()
    {
        var schedule = new FixedStages([(Milestone.Foundations, 200_000m), (Milestone.Roof, 60_000m)]);

        Assert.Throws<InvalidOperationException>(() => schedule.Plan(Amount(250_000m)));
    }

    [Theory]
    [InlineData(Milestone.Roof, 15_000)]
    [InlineData(Milestone.PracticalCompletion, 225_000)]
    [InlineData(Milestone.Frame, null)]
    public void Caller_asks_what_is_due_without_knowing_the_schedule(Milestone completed, int? expected)
    {
        IStagePaymentSchedule schedule = new FixedStages([(Milestone.Foundations, 10_000m), (Milestone.Roof, 15_000m)]);

        Assert.Equal(expected, (int?)schedule.DueOn(completed, Amount(250_000m))?.Amount.Amount);
    }
}
