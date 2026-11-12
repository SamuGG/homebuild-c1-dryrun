using Bunit;
using HomeBuild.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Web.Tests.Ui;

public sealed class PagerTests : UiTestContext
{
    public PagerTests() => Services.GetRequiredService<NavigationManager>().NavigateTo("developments/1?after=c1");

    [Fact]
    public void A_single_page_renders_no_pager()
    {
        var cut = Render<Pager>(p => p.Add(x => x.Next, null).Add(x => x.IsFirstPage, true));

        Assert.Empty(cut.FindAll("nav"));
    }

    [Fact]
    public void Next_cursor_becomes_a_next_link_in_a_named_nav_landmark()
    {
        var cut = Render<Pager>(p => p.Add(x => x.Next, "c2").Add(x => x.IsFirstPage, false).Add(x => x.Label, "Homes pages"));

        Assert.Equal("Homes pages", cut.Find("nav").GetAttribute("aria-label"));
        var next = cut.Find("a[rel=next]");
        Assert.EndsWith("developments/1?after=c2", next.GetAttribute("href"), StringComparison.Ordinal);
        Assert.EndsWith("developments/1", cut.FindAll("a")[0].GetAttribute("href"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_page_has_no_dead_next_link()
    {
        var cut = Render<Pager>(p => p.Add(x => x.Next, null).Add(x => x.IsFirstPage, false));

        Assert.Empty(cut.FindAll("a[rel=next]"));
        Assert.Equal("First page", Assert.Single(cut.FindAll("a")).TextContent);
    }
}
