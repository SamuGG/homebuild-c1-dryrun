using Microsoft.Extensions.Options;

namespace HomeBuild.Api.Configuration;

internal static partial class ConfigurationLog
{
    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Payments configured: {Payments}")]
    public static partial void PaymentsConfigured(this ILogger logger, string payments);
}

/// <summary>Logs the loaded payment settings once at startup, with secrets redacted.</summary>
internal sealed class ConfigurationStartupLogger(IOptions<PaymentsOptions> payments, ILogger<ConfigurationStartupLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.PaymentsConfigured(SecretRedaction.Describe(payments.Value));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ConfigurationSources
{
    /// <summary>
    /// In AWS (Staging, Production) configuration also comes from SSM Parameter Store under /homebuild/{env}/, read
    /// through the AWS configuration provider with the instance's credentials. It is added last, so it overrides
    /// appsettings. Set Ssm:Enabled to force it on or off (tests turn it off).
    /// </summary>
    public static void AddHomeBuildSecrets(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var inAws = builder.Environment.IsStaging() || builder.Environment.IsProduction();
        // Build-time OpenAPI generation (Microsoft.Extensions.ApiDescription.Server) starts the app as Production
        // through GetDocument.Insider; it needs endpoints, not secrets, and has no AWS credentials in CI.
        var generatingOpenApi = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
        if (!generatingOpenApi && builder.Configuration.GetValue("Ssm:Enabled", inAws))
        {
            builder.Configuration.AddSystemsManager(SsmPath(builder.Environment.EnvironmentName));
        }

        builder.Services.AddOptions<PaymentsOptions>().BindConfiguration(PaymentsOptions.Section);
        // Hosted services are singletons; this one depends only on IOptions (singleton).
        builder.Services.AddHostedService<ConfigurationStartupLogger>();
    }

    public static string SsmPath(string environmentName) =>
        $"/homebuild/{environmentName.ToLowerInvariant()}/";
}
