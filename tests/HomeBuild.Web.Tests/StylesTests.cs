using System.Text.RegularExpressions;

namespace HomeBuild.Web.Tests;

public sealed partial class StylesTests
{
    [Fact]
    public async Task Served_css_has_classes_used_only_in_razor_files()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        var html = await client.GetStringAsync(new Uri($"/units/{FakeCatalogueApi.PlotOne}", UriKind.Relative), cancellationToken);
        var href = AppCss().Match(html);
        Assert.True(href.Success, "The page does not link the generated app.css.");
        var css = await client.GetStringAsync(new Uri("/" + href.Groups["href"].Value, UriKind.Relative), cancellationToken);

        // grid-cols-[auto_1fr] appears only in UnitDetail.razor; rounded-card and bg-brand-700 come from @theme tokens.
        Assert.Contains(@".grid-cols-\[auto_1fr\]", css, StringComparison.Ordinal);
        Assert.Contains(".rounded-card", css, StringComparison.Ordinal);
        Assert.Contains("--color-brand-700", css, StringComparison.Ordinal);
    }

    [GeneratedRegex("""<link rel="stylesheet" href="(?<href>app\.[a-z0-9]+\.css)" """)]
    private static partial Regex AppCss();
}
