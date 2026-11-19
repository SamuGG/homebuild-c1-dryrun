using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeBuild.Messaging;

/// <summary>
/// C9 Lab 4: a scheduled check that logs a dead-letter queue's depth (a warning when anything is parked). It is the
/// placeholder for C14's CloudWatch alarm on ApproximateNumberOfMessagesVisible; until then the log is the signal.
/// </summary>
public sealed partial class DeadLetterMonitor(string name, IQueueClient deadLetters, TimeSpan interval, ILogger<DeadLetterMonitor> logger, TimeProvider time)
    : BackgroundService
{
    /// <summary>The depth at the last check (tests read it).</summary>
    public int? LastDepth { get; private set; }

    public async Task<int> CheckOnceAsync(CancellationToken cancellationToken)
    {
        var depth = await deadLetters.DepthAsync(cancellationToken);
        LastDepth = depth;
        if (depth > 0)
        {
            Parked(logger, name, depth);
        }
        else
        {
            Empty(logger, name);
        }

        return depth;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a failed check is logged and retried at the next tick; it must not stop the worker
            catch (Exception e)
#pragma warning restore CA1031
            {
                CheckFailed(logger, e, name);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 1711, Level = LogLevel.Warning, Message = "Dead-letter queue {Queue} holds {Depth} message(s): investigate, fix, then redrive or discard (C14 replaces this check with an alarm)")]
    private static partial void Parked(ILogger logger, string queue, int depth);

    [LoggerMessage(EventId = 1712, Level = LogLevel.Information, Message = "Dead-letter queue {Queue} is empty")]
    private static partial void Empty(ILogger logger, string queue);

    [LoggerMessage(EventId = 1713, Level = LogLevel.Warning, Message = "Dead-letter check for {Queue} failed")]
    private static partial void CheckFailed(ILogger logger, Exception exception, string queue);
}
