using Bunit;
using HomeBuild.Web.Catalogue;
using HomeBuild.Web.Components.Catalogue;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBuild.Web.Tests.Ui;

public sealed class UnitCardTests : UiTestContext
{
    public UnitCardTests() =>
        Services.AddSingleton(new CatalogueClient(new HttpClient(new FakeCatalogueApi(), disposeHandler: true) { BaseAddress = new Uri("http://api/v2/") }));

    [Fact]
    public void Unit_card_shows_the_new_unit_when_UnitId_changes()
    {
        var cut = Render<UnitCard>(p => p.Add(c => c.UnitId, FakeCatalogueApi.PlotOne));
        cut.WaitForAssertion(() => Assert.Equal("Plot P1", cut.Find("h3").TextContent));

        cut.Render(p => p.Add(c => c.UnitId, FakeCatalogueApi.PlotTwo));

        cut.WaitForAssertion(() => Assert.Equal("Plot P2", cut.Find("h3").TextContent));
    }
}
