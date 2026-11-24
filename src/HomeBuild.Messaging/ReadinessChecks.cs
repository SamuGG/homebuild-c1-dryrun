using System.Data.Common;
using Amazon.SQS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HomeBuild.Messaging;

/// <summary>C11 Lab 6, readiness: the queue answers (GetQueueAttributes). A deleted or unreachable queue is Unhealthy.</summary>
public sealed class QueueReachableCheck(IAmazonSQS sqs, string queueUrl) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"], cancellationToken);
            return HealthCheckResult.Healthy();
        }
#pragma warning disable CA1031 // any failure (missing queue, credentials, network) means "not ready"
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy($"Queue {queueUrl[(queueUrl.LastIndexOf('/') + 1)..]} unreachable: {e.GetType().Name}");
        }
    }
}

/// <summary>
/// C11 Lab 6, readiness: the database answers and every table the given contexts' models map to exists, which stands
/// for "migrations applied". The app role cannot read the migration history (C4 revokes it), but it can see the tables.
/// </summary>
public sealed class DatabaseReadyCheck(IServiceScopeFactory scopes, params Type[] contextTypes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var contexts = contextTypes.Select(t => (DbContext)scope.ServiceProvider.GetRequiredService(t)).ToList();
        var tables = contexts.SelectMany(c => c.Model.GetEntityTypes())
            .Where(e => e.GetTableName() is not null && e.GetViewName() is null)
            .Select(e => $"{e.GetSchema() ?? "public"}.{e.GetTableName()}")
            .Distinct().ToArray();
        try
        {
            var connection = contexts[0].Database.GetDbConnection();
            await contexts[0].Database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT n FROM unnest(@names) AS n WHERE to_regclass(n) IS NULL";
            var names = command.CreateParameter();
            names.ParameterName = "names";
            names.Value = tables;
            command.Parameters.Add(names);
            var missing = new List<string>();
            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    missing.Add(reader.GetString(0));
                }
            }

            return missing.Count == 0
                ? HealthCheckResult.Healthy($"{tables.Length} tables present")
                : HealthCheckResult.Unhealthy($"Migrations not applied: {missing.Count} of {tables.Length} tables missing (first: {missing[0]})");
        }
#pragma warning disable CA1031 // any database failure means "not ready"
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy($"Database unreachable: {e.GetType().Name}");
        }
    }
}
