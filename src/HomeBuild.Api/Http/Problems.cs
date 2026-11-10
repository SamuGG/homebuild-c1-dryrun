using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HomeBuild.Api.Http;

/// <summary>Business failures as RFC 9457 ProblemDetails, one type URI per failure.</summary>
internal static class Problems
{
    public const string TypeBase = "https://homebuild.dev/problems/";

    public static ProblemDetails Business(HttpContext http, string type, string title, int status, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        var problem = new ProblemDetails
        {
            Type = TypeBase + type,
            Title = title,
            Status = status,
            Detail = detail,
            Instance = http.Request.Path,
        };
        AddTraceId(problem, http);
        return problem;
    }

    public static void AddTraceId(ProblemDetails problem, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(http);
        problem.Extensions.TryAdd("traceId", Activity.Current?.Id ?? http.TraceIdentifier);
    }
}
