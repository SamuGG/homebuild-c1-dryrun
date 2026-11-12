using System.Reflection;
using System.Text.RegularExpressions;
using HomeBuild.Web.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;

namespace HomeBuild.Web.Tests;

/// <summary>C5 Lab 6 render-mode audit: interactivity (and its circuit) is allowed in one place only.</summary>
public sealed partial class RenderModeAuditTests
{
    private const string BoardPage = "Components/Pages/Staff/BuildBoard.razor";

    // The Development-only component gallery (/dev/components) is interactive on purpose; it 404s outside Development.
    private const string DevGallery = "Components/Pages/Dev/ComponentGallery.razor";

    private static readonly Assembly Web = typeof(HomeBuild.Web.Components.App).Assembly;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Board_is_the_only_interactive_component()
    {
        // 1. Source: every render mode in the app, where a component declares it or where a page uses one.
        var webRoot = Path.Combine(RepoRoot(), "src/HomeBuild.Web");
        var renderModes = Directory.EnumerateFiles(Path.Combine(webRoot, "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (File: Path.GetRelativePath(webRoot, file), Line: i + 1, Text: line.Trim())))
            .Where(l => RenderMode().IsMatch(l.Text))
            .ToList();
        Assert.Equal(
            [$"{DevGallery}: @rendermode InteractiveServer", $"{BoardPage}: <MilestoneBoard DevelopmentId=\"DevelopmentId\" @rendermode=\"InteractiveServer\" />"],
            renderModes.Select(l => $"{l.File}: {l.Text}").Order(StringComparer.Ordinal));

        // 2. Types: no component carries a render mode attribute except the gallery; nothing is interactive globally.
        var withAttribute = Web.GetTypes().Where(t => typeof(IComponent).IsAssignableFrom(t) && t.GetCustomAttributes<RenderModeAttribute>(true).Any())
            .Select(t => t.Name).ToList();
        Assert.Equal(["ComponentGallery"], withAttribute);

        // 3. Runtime: render every routable page as a user with every role; only the board page has a circuit marker.
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var browser = factory.CreateBrowser();
        await factory.SignInAsync(browser, new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)), "buyer", "admin", "site-manager");
        var markers = new Dictionary<string, int>();
        foreach (var route in RoutablePages().Where(r => r != "/dev/components"))
        {
            var html = await browser.GetStringAsync(new Uri(route, UriKind.Relative), Ct);
            markers[route] = ServerMarker().Count(html);
        }

        Assert.Equal(1, markers[$"/staff/developments/{FakeCatalogueApi.Riverside}/board"]);
        Assert.All(markers.Where(m => !m.Key.EndsWith("/board", StringComparison.Ordinal)), m => Assert.True(m.Value == 0, $"{m.Key} has {m.Value} interactive marker(s)"));
        Assert.True(markers.Count >= 9, string.Join(", ", markers.Keys)); // every page was rendered, not skipped
    }

    [Fact]
    public async Task Component_gallery_does_not_exist_outside_development()
    {
        await using var factory = new WebFactory(new FakeCatalogueApi());
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production")).CreateClient();

        using var response = await production.GetAsync(new Uri("/dev/components", UriKind.Relative), Ct);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // Each @page route, with its parameters filled with IDs the fake API knows.
    private static IEnumerable<string> RoutablePages() =>
        Web.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template)
            .Where(t => t is not "/not-found")
            .Select(t => t
                .Replace("{DevelopmentId:guid}", FakeCatalogueApi.Riverside.ToString(), StringComparison.Ordinal)
                .Replace("{UnitId:guid}", FakeCatalogueApi.PlotOne.ToString(), StringComparison.Ordinal)
                .Replace("{ReservationId:guid}", "0198a000-0000-7000-a000-000000000001", StringComparison.Ordinal))
            .Where(t => !t.Contains('{', StringComparison.Ordinal) && !t.Contains("/reservations/", StringComparison.Ordinal))
            .Distinct();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "HomeBuild.slnx")))
        {
            dir = dir.Parent;
        }

        return dir.FullName;
    }

    [GeneratedRegex(@"@rendermode\b")]
    private static partial Regex RenderMode();

    // The prerender marker of a server-interactive component: <!--Blazor:{"type":"server",...}-->
    [GeneratedRegex(@"<!--Blazor:\{""type"":""server""")]
    private static partial Regex ServerMarker();
}
