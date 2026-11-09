namespace HomeBuild.Domain.Tests;

public class IdTests
{
    [Fact]
    public void New_ids_are_unique_and_not_empty()
    {
        var a = UnitId.New();
        var b = UnitId.New();
        Assert.NotEqual(a, b);
        Assert.NotEqual(Guid.Empty, a.Value);
    }

    [Fact]
    public void Ids_with_same_guid_are_equal()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(new UnitId(guid), new UnitId(guid));
    }

    [Fact]
    public void UnitId_round_trips_through_ToString_and_TryParse()
    {
        var id = UnitId.New();
        Assert.True(UnitId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void TryParse_rejects_invalid_text()
    {
        Assert.False(UnitId.TryParse("not-a-guid", out _));
        Assert.False(UnitId.TryParse(null, out _));
    }

    [Fact]
    public void Unit_rejects_default_id() =>
        Assert.Throws<ArgumentException>(() =>
            new Unit(default, DevelopmentId.New(), "P-01", new Money(1m, "GBP")));
}
