namespace HomeBuild.Api.Http;

internal static partial class ErrorLog
{
    [LoggerMessage(EventId = 1900, Level = LogLevel.Error,
        Message = "Unhandled exception for {Method} {Path}, trace {TraceId}")]
    public static partial void UnhandledException(this ILogger logger, Exception exception, string method, string path, string traceId);
}
