using System.Globalization;
using System.Text;
using HomeBuild.Api.Data;
using HomeBuild.Api.Modules.Accounts;
using HomeBuild.Domain;
using HomeBuild.Payments;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;

namespace HomeBuild.Api.Modules.Payments.Reconcile;

internal sealed record ReconciliationReport(DateTimeOffset From, DateTimeOffset To, int StripeSessions, int StripePaidSessions, int StripeRefunds, int Deposits, IReadOnlyList<Mismatch> Mismatches);

/// <summary>
/// C8 Lab 6: <c>dotnet run --project src/HomeBuild.Api -- reconcile --from &lt;date&gt; --to &lt;date&gt; [--out file.csv]</c>.
/// Lists Stripe's Checkout Sessions and refunds created in the window, reads HomeBuild's deposits for every tenant,
/// compares them (the rules are in Payments' core) and writes a CSV and a summary log. C9 runs it on a schedule.
/// </summary>
internal sealed partial class ReconcileCommand(IStripeClient stripe, IConfiguration configuration, ILogger<ReconcileCommand> logger)
{
    public static async Task<int> RunFromCommandLineAsync(IServiceProvider services, string[] args)
    {
        string Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"reconcile needs {name}.");
        var from = ParseUtc(Value("--from"));
        var to = ParseUtc(Value("--to"));
        var output = Array.IndexOf(args, "--out") >= 0 ? Value("--out") : $"reconciliation-{from:yyyyMMddTHHmm}-{to:yyyyMMddTHHmm}.csv";
        await using var scope = services.CreateAsyncScope();
        var command = ActivatorUtilities.CreateInstance<ReconcileCommand>(scope.ServiceProvider);
        var report = await command.RunAsync(from, to, CancellationToken.None);
        await System.IO.File.WriteAllTextAsync(output, ToCsv(report));
        Console.WriteLine($"Reconciliation {from:u} to {to:u}: {report.StripeSessions} Stripe sessions ({report.StripePaidSessions} paid), {report.StripeRefunds} refunds, {report.Deposits} HomeBuild deposits, {report.Mismatches.Count} mismatches. CSV: {output}");
        return report.Mismatches.Count == 0 ? 0 : 2;
    }

    public async Task<ReconciliationReport> RunAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var window = new DateRangeOptions { GreaterThanOrEqual = from.UtcDateTime, LessThan = to.UtcDateTime };
        var sessions = new List<ProviderSession>();
        await foreach (var s in new SessionService(stripe).ListAutoPagingAsync(new SessionListOptions { Created = window, Limit = 100 }, cancellationToken: cancellationToken))
        {
            sessions.Add(ToProvider(s));
        }

        var refunds = new List<ProviderRefund>();
        await foreach (var r in new RefundService(stripe).ListAutoPagingAsync(new RefundListOptions { Created = window, Limit = 100 }, cancellationToken: cancellationToken))
        {
            refunds.Add(new ProviderRefund(r.Id, r.PaymentIntentId, StripeAmounts.FromMinorUnits(r.Amount, r.Currency), r.Status, Utc(r.Created)));
        }

        // HomeBuild's side, tenant by tenant (row-level security): deposits paid in the window, and any deposit for a
        // session Stripe listed (a session created in the window may be paid after it, or the other way round).
        var connection = configuration.GetConnectionString(HomeBuildDatabase.ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{HomeBuildDatabase.ConnectionStringName}' is missing.");
        var sessionIds = sessions.Select(s => s.Id).ToList();
        var deposits = new List<RecordedDeposit>();
        foreach (var tenant in await TenantsAsync(connection, sessions, cancellationToken))
        {
            await using var db = PaymentsModule.CreateDbContext(connection, tenant);
            deposits.AddRange((await db.Deposits.AsNoTracking()
                    .Where(d => sessionIds.Contains(d.SessionId) || (d.PaidAt >= from && d.PaidAt < to))
                    .ToListAsync(cancellationToken))
                .Select(d => new RecordedDeposit(d.SessionId, d.ReservationId, tenant, new Money(d.Amount, d.Currency), d.ProviderReference, d.PaidAt, d.RefundReason is not null, d.RefundReference)));
        }

        // Sessions HomeBuild recorded that Stripe did not list for the window: ask for each one.
        foreach (var deposit in deposits.Where(d => !sessionIds.Contains(d.SessionId)))
        {
            try
            {
                sessions.Add(ToProvider(await new SessionService(stripe).GetAsync(deposit.SessionId, cancellationToken: cancellationToken)));
            }
            catch (StripeException e) when (e.StripeError?.Code == "resource_missing")
            {
                // Not in Stripe at all: Compare reports it as recorded but not paid.
            }
        }

        var listed = sessionIds.Count;
        var paid = sessions.Count(s => s.Paid);
        var mismatches = Reconciliation.Compare(sessions, refunds, deposits);
        var counts = Enum.GetValues<MismatchKind>().ToDictionary(k => k, k => mismatches.Count(m => m.Kind == k));
        foreach (var (kind, count) in counts)
        {
            MismatchCount(logger, kind, count);
        }

        Summary(logger, from, to, listed, paid, refunds.Count, deposits.Count, mismatches.Count);
        return new ReconciliationReport(from, to, listed, paid, refunds.Count, deposits.Count, mismatches);
    }

    public static string ToCsv(ReconciliationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var csv = new StringBuilder("kind,session_id,reservation_id,stripe_amount,homebuild_amount,currency,detail\n");
        foreach (var m in report.Mismatches)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{m.Kind},{m.SessionId},{m.ReservationId?.Value},{m.ProviderAmount?.Amount},{m.HomeBuildAmount?.Amount},{(m.ProviderAmount ?? m.HomeBuildAmount)?.Currency},\"{m.Detail.Replace("\"", "\"\"", StringComparison.Ordinal)}\"\n");
        }

        return csv.ToString();
    }

    private static async Task<IReadOnlyCollection<Guid>> TenantsAsync(string connection, IEnumerable<ProviderSession> sessions, CancellationToken cancellationToken)
    {
        await using var accounts = AccountsModule.CreateDbContext(connection);
        var known = await accounts.TenantMemberships.AsNoTracking().Select(m => m.TenantId).Distinct().ToListAsync(cancellationToken);
        return [.. known.Concat(sessions.Where(s => s.TenantId is not null).Select(s => s.TenantId!.Value)).Distinct()];
    }

    private static ProviderSession ToProvider(Session s) => new(
        s.Id,
        Guid.TryParse(s.ClientReferenceId, out var reservation) ? new ReservationId(reservation) : null,
        s.Metadata is not null && s.Metadata.TryGetValue(StripePaymentGateway.TenantMetadata, out var t) && Guid.TryParse(t, out var tenant) ? tenant : null,
        s.PaymentStatus == "paid",
        s.AmountTotal is { } amount && s.Currency is { } currency ? StripeAmounts.FromMinorUnits(amount, currency) : null,
        s.PaymentIntentId,
        Utc(s.Created));

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Reconciliation mismatches of kind {Kind}: {Count}")]
    private static partial void MismatchCount(ILogger logger, MismatchKind kind, int count);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Information, Message = "Reconciliation {From} to {To}: {Sessions} Stripe sessions ({Paid} paid), {Refunds} refunds, {Deposits} HomeBuild deposits, {Mismatches} mismatches")]
    private static partial void Summary(ILogger logger, DateTimeOffset from, DateTimeOffset to, int sessions, int paid, int refunds, int deposits, int mismatches);
}
