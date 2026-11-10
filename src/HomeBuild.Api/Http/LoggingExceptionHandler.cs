using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace HomeBuild.Api.Http;

/// <summary>Logs unhandled exceptions with a generated log method, then lets the default handler write a 500 ProblemDetails.</summary>
internal sealed class LoggingExceptionHandler(ILogger<LoggingExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        logger.UnhandledException(exception, httpContext.Request.Method, httpContext.Request.Path, Activity.Current?.Id ?? httpContext.TraceIdentifier);
        return ValueTask.FromResult(false);
    }
}
