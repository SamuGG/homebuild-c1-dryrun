using Bunit;
using HomeBuild.Web.Components.Ui;
using Microsoft.AspNetCore.Components;

namespace HomeBuild.Web.Tests.Ui;

public sealed class DataTableTests : UiTestContext
{
    private static readonly RenderFragment Header = b =>
    {
        b.OpenElement(0, "th");
        b.AddAttribute(1, "scope", "col");
        b.AddContent(2, "Plot");
        b.CloseElement();
    };

    [Fact]
    public void Renders_a_captioned_table_with_one_row_per_item()
    {
        var cut = Render<DataTable<string>>(p => p
            .Add(t => t.Caption, "Homes at Riverside")
            .Add(t => t.Items, ["P1", "P2"])
            .Add(t => t.HeaderContent, Header)
            .Add(t => t.RowTemplate, item => b => b.AddMarkupContent(0, $"<td>{item}</td>")));

        Assert.Equal("Homes at Riverside", cut.Find("table > caption").TextContent);
        Assert.Equal("col", cut.Find("thead th").GetAttribute("scope"));
        Assert.Equal(["P1", "P2"], cut.FindAll("tbody td").Select(td => td.TextContent));
    }

    [Fact]
    public void No_items_renders_the_empty_content_instead_of_an_empty_table()
    {
        var cut = Render<DataTable<string>>(p => p
            .Add(t => t.Caption, "Homes")
            .Add(t => t.Items, [])
            .Add(t => t.HeaderContent, Header)
            .Add(t => t.EmptyContent, b => b.AddMarkupContent(0, "<p>No homes listed yet.</p>")));

        Assert.Empty(cut.FindAll("table"));
        cut.MarkupMatches("<p>No homes listed yet.</p>");
    }
}
