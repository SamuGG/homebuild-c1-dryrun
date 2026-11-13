using HomeBuild.Testing;
using static HomeBuild.Testing.Builders;

namespace HomeBuild.Domain.Tests;

public class BuilderTests
{
    [Fact]
    public void Builders_cover_every_aggregate()
    {
        // An aggregate here is a domain class with an identity: a public Id property of a strongly typed ID. BuildProgress
        // (C7) is the exception: one per unit, its identity is the unit's ID, so it has no Id property and is named here.
        var aggregates = typeof(Unit).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && t.GetProperty("Id")?.PropertyType.Name.EndsWith("Id", StringComparison.Ordinal) == true)
            .Select(t => t.Name)
            .Append(nameof(BuildProgress))
            .Order(StringComparer.Ordinal)
            .ToList();
        var builders = typeof(Builders).Assembly.GetExportedTypes().Select(t => t.Name).ToHashSet();

        Assert.Equal(["BuildProgress", "DiaryEntry", "Reservation", "Unit"], aggregates);
        Assert.All(aggregates, a => Assert.True(builders.Contains(a + "Builder"), $"No {a}Builder in HomeBuild.Testing."));
    }

    [Fact]
    public void Default_unit_is_valid_and_available()
    {
        var unit = AUnit().Build();

        Assert.Equal(UnitStatus.Available, unit.Status);
        Assert.Equal(DefaultCurrency, unit.Price.Currency);
    }

    [Fact]
    public void Reserved_unit_is_reserved_in_the_named_development()
    {
        var development = DevelopmentId.New();

        var unit = AUnit().Reserved().InDevelopment(development).Build();

        Assert.Equal(UnitStatus.Reserved, unit.Status);
        Assert.Equal(development, unit.DevelopmentId);
    }

    [Fact]
    public void Reservation_builder_reaches_every_state()
    {
        Assert.IsType<Held>(AReservation().Build().State);
        Assert.IsType<Confirmed>(AReservation().Confirmed().Build().State);
        Assert.IsType<Cancelled>(AReservation().Cancelled().Build().State);
        Assert.IsType<Expired>(AReservation().Expired().Build().State);
    }

    [Fact]
    public void Default_policy_accepts_the_default_deposit()
    {
        var policy = APolicy().Build();

        Assert.True(AReservation().Build().Deposit.Amount >= policy.MinimumDeposit.Amount);
    }
}
