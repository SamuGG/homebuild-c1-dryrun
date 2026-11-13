using Bunit;
using HomeBuild.Web.Api;
using HomeBuild.Web.Components.Construction;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Web.Tests.Ui;

public sealed class MilestoneBoardTests : UiTestContext
{
    public MilestoneBoardTests()
    {
        Services.AddSingleton(new HomeApiClient(new HttpClient(new FakeCatalogueApi(), disposeHandler: true) { BaseAddress = new Uri("http://api/v2/") }));
        // The board reads RendererInfo for its data-interactive marker; here it renders as the interactive island it is.
        Renderer.SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
    }

    [Fact]
    public void Board_marks_itself_interactive_only_when_it_is()
    {
        var cut = Render<MilestoneBoard>(p => p.Add(b => b.DevelopmentId, FakeCatalogueApi.Riverside));

        Assert.Equal("true", cut.Find("[data-testid=board-root]").GetAttribute("data-interactive"));
    }

    [Fact]
    public void Board_shows_units_as_rows_and_milestones_as_columns_with_site_detail()
    {
        var cut = Render<MilestoneBoard>(p => p.Add(b => b.DevelopmentId, FakeCatalogueApi.Riverside));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tbody tr").Count));

        Assert.Equal(["Plot", "Foundations", "Frame", "Roof"], cut.FindAll("thead th").Select(th => th.TextContent));
        Assert.Equal(["P1", "P2", "P3"], cut.FindAll("tbody th[scope=row]").Select(th => th.TextContent));
        Assert.Contains("2 open defects", cut.Find("tbody tr").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_by_stage_and_plot_search_narrow_the_rows()
    {
        var cut = Render<MilestoneBoard>(p => p.Add(b => b.DevelopmentId, FakeCatalogueApi.Riverside));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tbody tr").Count));

        cut.Find("#stage").Change("Roof");
        Assert.Equal(["P2"], cut.FindAll("tbody th[scope=row]").Select(th => th.TextContent));
        Assert.Equal("1 of 3 plots", cut.Find("[data-testid=row-count]").TextContent);

        cut.Find("#stage").Change("Finished");
        Assert.Equal(["P3"], cut.FindAll("tbody th[scope=row]").Select(th => th.TextContent));

        cut.Find("#stage").Change("");
        cut.Find("#plot-search").Input("p1");
        Assert.Equal(["P1"], cut.FindAll("tbody th[scope=row]").Select(th => th.TextContent));
    }
}
