using HomeBuild.Domain;
using Microsoft.EntityFrameworkCore;

namespace HomeBuild.Payments.Infrastructure;

/// <summary>A paid deposit as stored (schema "payments"). Not wired into the API or migrated until C8.</summary>
internal sealed class PaymentRecord
{
    public Guid ReservationId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "";

    public string ProviderReference { get; set; } = "";

    public DateTimeOffset PaidAt { get; set; }
}

internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRecord> Payments => Set<PaymentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payments");
        modelBuilder.Entity<PaymentRecord>(payment =>
        {
            payment.HasKey(p => p.ReservationId); // one payment per reservation
            payment.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            payment.Property(p => p.ProviderReference).HasMaxLength(200);
        });
    }
}

/// <summary>EF Core adapter for <see cref="IPaymentRecords"/>.</summary>
internal sealed class EfPaymentRecords(PaymentsDbContext db) : IPaymentRecords
{
    public async Task<DepositPaid?> FindAsync(ReservationId reservationId, CancellationToken cancellationToken) =>
        await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.ReservationId == reservationId.Value, cancellationToken) is { } p
            ? new DepositPaid(reservationId, new Money(p.Amount, p.Currency), p.ProviderReference, p.PaidAt)
            : null;

    public async Task AddAsync(DepositPaid paid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paid);
        db.Payments.Add(new PaymentRecord
        {
            ReservationId = paid.ReservationId.Value,
            Amount = paid.Amount.Amount,
            Currency = paid.Amount.Currency,
            ProviderReference = paid.ProviderReference,
            PaidAt = paid.PaidAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
