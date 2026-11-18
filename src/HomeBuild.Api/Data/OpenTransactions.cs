using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HomeBuild.Api.Data;

/// <summary>
/// C8 Lab 4: counts the database transactions open in this request (scoped; added to every module's DbContext). Code
/// that calls out of process (Stripe, S3, email) asks it first: an external call cannot be rolled back, so it never
/// happens inside a transaction. The order is write intent, commit, call out, record the result.
/// </summary>
internal sealed class OpenTransactions : DbTransactionInterceptor
{
    // The transactions this request started. Counting commit and rollback events is not enough: EF raises no event
    // when an uncommitted transaction is only disposed (rolled back by the database), so each one is asked instead.
    private readonly List<DbTransaction> _started = [];

    public bool Any
    {
        get
        {
            lock (_started)
            {
                _started.RemoveAll(t => !IsOpen(t));
                return _started.Count > 0;
            }
        }
    }

    /// <summary>Throws if a transaction is open: the caller is about to make <paramref name="call"/>.</summary>
    public void EnsureNone(string call)
    {
        if (Any)
        {
            throw new InvalidOperationException($"{call} inside a database transaction: external calls cannot be rolled back. Commit first, then call out, then record the result (CLAUDE.md).");
        }
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Track(result);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Track(result);
        return ValueTask.FromResult(result);
    }

    private void Track(DbTransaction transaction)
    {
        lock (_started)
        {
            _started.Add(transaction);
        }
    }

    // Npgsql: a committed, rolled back or disposed transaction has no connection.
    private static bool IsOpen(DbTransaction transaction)
    {
        try
        {
            return transaction.Connection is not null;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
