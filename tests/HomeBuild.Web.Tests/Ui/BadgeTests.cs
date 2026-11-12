using Bunit;
using HomeBuild.Web.Components.Ui;

namespace HomeBuild.Web.Tests.Ui;

public sealed class BadgeTests : UiTestContext
{
    [Theory]
    [InlineData(BadgeVariant.Success, "bg-brand-100")]
    [InlineData(BadgeVariant.Warning, "bg-accent-500")]
    [InlineData(BadgeVariant.Neutral, "bg-white")]
    public void Variant_sets_the_colour_and_the_text_is_always_shown(BadgeVariant variant, string colour)
    {
        var cut = Render<Badge>(p => p.Add(b => b.Text, "Reserved").Add(b => b.Variant, variant));

        var badge = cut.Find("span");
        Assert.Contains(colour, badge.ClassName, StringComparison.Ordinal);
        Assert.Equal("Reserved", badge.TextContent); // colour is never the only signal (WCAG 1.4.1)
    }

    [Theory]
    [InlineData("Available", BadgeVariant.Success)]
    [InlineData("Reserved", BadgeVariant.Warning)]
    [InlineData("Sold", BadgeVariant.Neutral)]
    public void Unit_status_maps_to_a_variant(string status, BadgeVariant expected) =>
        Assert.Equal(expected, Badge.ForUnitStatus(status));
}
