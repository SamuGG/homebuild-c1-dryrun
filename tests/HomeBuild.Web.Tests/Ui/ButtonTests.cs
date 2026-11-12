using Bunit;
using HomeBuild.Web.Components.Ui;

namespace HomeBuild.Web.Tests.Ui;

public sealed class ButtonTests : UiTestContext
{
    [Fact]
    public void Renders_a_native_button_of_type_button_with_a_visible_focus_ring()
    {
        var cut = Render<Button>(p => p.AddChildContent("Save"));

        var button = cut.Find("button");
        Assert.Equal("button", button.GetAttribute("type")); // never submits a form by accident
        Assert.Contains("focus-visible:outline-2", button.ClassName, StringComparison.Ordinal);
        Assert.Equal("Save", button.TextContent);
    }

    [Fact]
    public void Secondary_variant_and_extra_attributes_are_applied()
    {
        var cut = Render<Button>(p => p
            .Add(b => b.Variant, ButtonVariant.Secondary)
            .AddUnmatched("aria-pressed", "true")
            .AddChildContent("P1"));

        var button = cut.Find("button");
        Assert.Contains("border-brand-700", button.ClassName, StringComparison.Ordinal);
        Assert.Equal("true", button.GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Click_raises_OnClick_unless_disabled()
    {
        var clicks = 0;
        var cut = Render<Button>(p => p.Add(b => b.OnClick, () => clicks++).AddChildContent("Go"));

        cut.Find("button").Click();
        cut.Render(p => p.Add(b => b.Disabled, true));

        Assert.Equal(1, clicks);
        Assert.True(cut.Find("button").HasAttribute("disabled"));
    }
}
