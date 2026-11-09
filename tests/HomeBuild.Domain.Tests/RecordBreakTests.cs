namespace HomeBuild.Domain.Tests;

// Lab 2 break steps, written as tests that document the behaviour.
public class RecordBreakTests
{
    private sealed record Tagged(List<string> Tags);

    private readonly record struct MoneyStruct(decimal Amount, string Currency);

    [Fact]
    public void Records_with_equal_list_contents_are_not_equal() =>
        Assert.NotEqual(new Tagged(["a"]), new Tagged(["a"]));

    [Fact]
    public void With_makes_a_shallow_copy()
    {
        var original = new Tagged(["a"]);
        var copy = original with { };
        copy.Tags.Add("b");
        Assert.Equal(["a", "b"], original.Tags);
    }

    [Fact]
    public void Default_struct_skips_the_constructor() =>
        Assert.Null(default(MoneyStruct).Currency);
}
