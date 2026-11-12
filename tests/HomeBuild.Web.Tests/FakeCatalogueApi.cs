using System.Net;
using System.Net.Http.Json;
using HomeBuild.Web.Catalogue;

namespace HomeBuild.Web.Tests;

/// <summary>Stands in for the HomeBuild API. Unit-list calls can be held until the test releases them.</summary>
internal sealed class FakeCatalogueApi : HttpMessageHandler
{
    public static readonly Guid Riverside = Guid.Parse("0198a000-0000-7000-8000-000000000001");
    public static readonly Guid PlotOne = Guid.Parse("0198a000-0000-7000-8001-000000000001");
    public static readonly Guid PlotTwo = Guid.Parse("0198a000-0000-7000-8001-000000000002");

    private readonly TaskCompletionSource unitsReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeCatalogueApi(bool holdUnits = false)
    {
        if (!holdUnits)
        {
            unitsReleased.SetResult();
        }
    }

    /// <summary>Completes when a units call has started (and is waiting, if held).</summary>
    public TaskCompletionSource UnitsRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<HttpRequestMessage> Requests { get; } = [];

    public void ReleaseUnits() => unitsReleased.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var path = request.RequestUri!.AbsolutePath;
        if (path == "/v2/developments")
        {
            return Json(new DevelopmentPage([new(Riverside, "Riverside", "York")], 1, 100, 1));
        }

        if (path == $"/v2/developments/{Riverside}")
        {
            return Json(new Development(Riverside, "Riverside", "York"));
        }

        if (path == $"/v2/developments/{Riverside}/units")
        {
            UnitsRequested.TrySetResult();
            await unitsReleased.Task.WaitAsync(cancellationToken);
            return Json(new UnitPage([new(PlotOne, "P1", 260_000m, "GBP", "Available")], null));
        }

        if (path == $"/v2/units/{PlotOne}")
        {
            return Json(new Unit(PlotOne, Riverside, "P1", new Price(260_000m, "GBP"), "Available"));
        }

        if (path == $"/v2/units/{PlotTwo}")
        {
            return Json(new Unit(PlotTwo, Riverside, "P2", new Price(270_000m, "GBP"), "Reserved"));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}
