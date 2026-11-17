using System.Data.Common;
using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HomeBuild.Api.Data;

/// <summary>
/// Calls every handler registered for an event, in the current scope. Each handler is isolated: one that throws is
/// logged and the others still run, because the change that raised the event is already committed and cannot be
/// undone (C7 Lab 5 Break). Its work is lost until C9's outbox retries it.
/// </summary>
internal sealed partial class DomainEventDispatcher(IServiceProvider services, ILogger<DomainEventDispatcher> logger)
{
    public async Task DispatchAsync(IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in events)
        {
            var handlerType = typeof(IHandleDomainEvent<>).MakeGenericType(domainEvent.GetType());
            foreach (var handler in services.GetServices(handlerType))
            {
                try
                {
                    await (Task)handlerType.GetMethod(nameof(IHandleDomainEvent<IDomainEvent>.HandleAsync))!
                        .Invoke(handler, [domainEvent, cancellationToken])!;
                }
#pragma warning disable CA1031 // one handler's failure must not stop the others or fail a committed request
                catch (Exception e)
#pragma warning restore CA1031
                {
                    HandlerFailed(logger, e, handler!.GetType().Name, domainEvent.ToString()!);
                }
            }
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Error, Message = "Domain event handler {Handler} failed for {Event}; the change is committed, the handler's work is not")]
    private static partial void HandlerFailed(ILogger logger, Exception exception, string handler, string @event);
}

/// <summary>
/// Collects domain events from tracked aggregates when EF Core saves, and dispatches them to in-process handlers only
/// after the transaction commits: the commit of SaveChanges' own transaction, or of the caller's explicit transaction.
/// A failed save or a rollback drops them. One instance per DbContext (the options are scoped), so the pending list
/// belongs to one unit of work.
/// <para>What this still loses (C9's outbox closes it): a crash between the commit and the end of dispatch loses the
/// events not yet handled, with the milestone already saved.</para>
/// </summary>
internal sealed class DomainEventInterceptor(IServiceProvider services) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly List<IDomainEvent> pending = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Collect(eventData.Context!);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Collect(eventData.Context!);
        return result;
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        pending.Clear();
        return Task.CompletedTask;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => pending.Clear();

    // Called for SaveChanges' own transaction and for one the caller began: either way, the data is now committed.
    public async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var events = pending.ToList();
        pending.Clear();
        await services.GetRequiredService<DomainEventDispatcher>().DispatchAsync(events, cancellationToken);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        TransactionCommittedAsync(transaction, eventData).GetAwaiter().GetResult();

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        pending.Clear();
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => pending.Clear();

    private void Collect(DbContext context)
    {
        var aggregates = context.ChangeTracker.Entries<IHasDomainEvents>().Select(e => e.Entity).ToList();
        pending.AddRange(aggregates.SelectMany(a => a.DomainEvents));
        aggregates.ForEach(a => a.ClearDomainEvents());
    }
}
