using Bunit;
using HomeBuild.Web.Components.Ui;

namespace HomeBuild.Web.Tests.Ui;

public sealed class CardTests : UiTestContext
{
    [Fact]
    public void Card_is_an_article_named_by_its_heading()
    {
        var cut = Render<Card>(p => p.Add(c => c.Title, "Riverside").AddChildContent("<p>York</p>"));

        var article = cut.Find("article");
        var heading = cut.Find("h2");
        Assert.Equal(heading.Id, article.GetAttribute("aria-labelledby"));
        Assert.Equal("Riverside", heading.TextContent);
        cut.Find("p").MarkupMatches("<p>York</p>");
    }

    [Fact]
    public void Href_makes_the_title_the_cards_only_link_and_heading_level_is_honoured()
    {
        var cut = Render<Card>(p => p.Add(c => c.Title, "Riverside").Add(c => c.Href, "developments/1").Add(c => c.HeadingLevel, 3));

        var link = Assert.Single(cut.FindAll("a"));
        Assert.Equal("developments/1", link.GetAttribute("href"));
        Assert.Equal("H3", link.ParentElement!.TagName);
    }
}
