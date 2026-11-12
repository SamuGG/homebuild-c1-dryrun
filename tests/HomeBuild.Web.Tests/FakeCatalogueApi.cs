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

    /// <summary>Answers a request before the defaults (null: fall through). Used for writes and error cases.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>The body of each request with content, read when it was sent.</summary>
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Bodies)
            {
                Bodies.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {body}");
            }
        }

        if (Respond?.Invoke(request) is { } answer)
        {
            return answer;
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

        if (path == $"/v2/developments/{Riverside}/board")
        {
            return Json(new HomeBuild.Web.Api.Board(Riverside, "Riverside", ["Foundations", "Frame", "Roof"],
            [
                new(PlotOne, "P1", [Cell("Foundations", "Done"), Cell("Frame", "InProgress"), Cell("Roof", "NotStarted")]),
                new(PlotTwo, "P2", [Cell("Foundations", "Done"), Cell("Frame", "Done"), Cell("Roof", "InProgress")]),
                new(Guid.Parse("0198a000-0000-7000-8001-000000000003"), "P3", [Cell("Foundations", "Done"), Cell("Frame", "Done"), Cell("Roof", "Done")]),
            ]));
        }

        if (path == "/v2/me/progress")
        {
            return Json(new HomeBuild.Web.Api.MyProgress(
            [
                new(Guid.Parse("0198a000-0000-7000-a000-000000000001"), PlotOne, "P1", "Riverside", "Pending", DateTimeOffset.UtcNow.AddDays(7),
                    new HomeBuild.Web.Api.BuyerProgress("Frame", 1, 3, "Roof")),
            ]));
        }

        if (path == "/v2/me")
        {
            // Like the API: no bearer token, no user.
            return request.Headers.Authorization is { Scheme: "Bearer" }
                ? Json(new HomeBuild.Web.Api.CurrentUser("sub-1", "web-test", ["buyer"], Guid.Parse("0198a000-0000-7000-8000-00000000000a")))
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
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

    private static HomeBuild.Web.Api.BoardCell Cell(string milestone, string status) =>
        new(milestone, status, status == "Done" ? new DateOnly(2026, 7, 1) : null, "Ouse Timber Frames", status == "InProgress" ? "Awaiting inspection sign-off" : "", status == "InProgress" ? 2 : 0);

    public static HttpResponseMessage Json<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };

    /// <summary>A ProblemDetails response, as the API sends it.</summary>
    public static HttpResponseMessage Problem(HttpStatusCode status, string title, string? detail = null, Dictionary<string, string[]>? errors = null) =>
        new(status)
        {
            Content = JsonContent.Create(new { type = "about:blank", title, status = (int)status, detail, errors },
                mediaType: new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json")),
        };
}
