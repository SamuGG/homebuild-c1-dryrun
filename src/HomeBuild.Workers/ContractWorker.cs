using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using HomeBuild.Domain;
using HomeBuild.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeBuild.Workers;

/// <summary>Port: where contract packs are stored (S3 in the host, in memory in tests).</summary>
internal interface IContractDocuments
{
    Task PutAsync(string key, byte[] pdf, CancellationToken cancellationToken);
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string? BucketName { get; set; }
}

internal sealed class S3ContractDocuments(IAmazonS3 s3, IOptions<StorageOptions> options) : IContractDocuments
{
    public Task PutAsync(string key, byte[] pdf, CancellationToken cancellationToken) =>
        s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Value.BucketName ?? throw new InvalidOperationException("Storage:BucketName is not set."),
            Key = key,
            InputStream = new MemoryStream(pdf),
            ContentType = "application/pdf",
        }, cancellationToken);
}

/// <summary>
/// C9 Lab 3: on DepositPaid, generate the contract pack (a PDF stub) to S3 under <c>contracts/</c> and record
/// ContractIssued through the outbox. The order keeps the external call outside the transaction (C8 Lab 4): the PDF is
/// written first, under a key fixed by the reservation (a retry overwrites the same object), then the inbox row, the
/// contract and the outbox row commit together. A duplicate finds its inbox row and does nothing.
/// </summary>
internal sealed class ContractWorker(ContractsDbContext db, IContractDocuments documents, TimeProvider time) : IMessageHandler
{
    public const string Name = "contracts";

    public async Task HandleAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Type != IntegrationEvents.DepositPaid)
        {
            return; // not ours (the filter policy should not send it): deleted
        }

        // Bad data is permanent (Lab 4): no retry fixes it, so it goes to the dead-letter queue without back-off.
        var messageId = message.MessageId ?? throw new PermanentMessageException($"Message {message.ProviderId} has no message_id attribute.");
        if (await db.Set<InboxMessage>().AnyAsync(m => m.Consumer == Name && m.MessageId == messageId, cancellationToken))
        {
            return; // processed before: a duplicate delivery
        }

        var paid = Parse(messageId, message.Body);
        var key = $"contracts/{paid.TenantId}/{paid.ReservationId}.pdf";
        await documents.PutAsync(key, ContractPdf(paid), cancellationToken);

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await Inbox.TryRecordAsync(db, Name, messageId, time.GetUtcNow(), ct))
            {
                await transaction.CommitAsync(ct);
                return; // a concurrent duplicate got here first
            }

            if (!await db.Contracts.AnyAsync(c => c.ReservationId == paid.ReservationId, ct))
            {
                var issuedAt = time.GetUtcNow();
                db.Contracts.Add(new ContractRow { ReservationId = paid.ReservationId, UnitId = paid.UnitId, TenantId = paid.TenantId, DocumentKey = key, IssuedAt = issuedAt, MessageId = messageId });
                db.Set<OutboxMessage>().Add(OutboxMessage.For(IntegrationEvents.ContractIssued,
                    new ContractIssuedMessage(paid.ReservationId, paid.UnitId, paid.TenantId, key, issuedAt), paid.TenantId, issuedAt));
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }

    /// <summary>The payload, or a permanent failure: unreadable JSON, or no reservation, unit or tenant to issue a contract for.</summary>
    internal static DepositPaidMessage Parse(Guid messageId, string body)
    {
        DepositPaidMessage? paid;
        try
        {
            paid = JsonSerializer.Deserialize<DepositPaidMessage>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException e)
        {
            throw new PermanentMessageException($"Message {messageId} is not a DepositPaid payload.", e);
        }

        return paid switch
        {
            null => throw new PermanentMessageException($"Message {messageId} has no body."),
            { ReservationId: var r } when r == Guid.Empty => throw new PermanentMessageException($"Message {messageId} names no reservation."),
            { UnitId: var u } when u == Guid.Empty => throw new PermanentMessageException($"Message {messageId} names an unknown unit."),
            { TenantId: var t } when t == Guid.Empty => throw new PermanentMessageException($"Message {messageId} names no tenant."),
            _ => paid,
        };
    }

    /// <summary>A stub contract pack: a minimal one-page PDF naming the reservation (C10 replaces it with the real pack).</summary>
    internal static byte[] ContractPdf(DepositPaidMessage paid)
    {
        var text = $"HomeBuild contract pack - reservation {paid.ReservationId}, deposit {paid.Amount} {paid.Currency}";
        var content = $"BT /F1 12 Tf 50 750 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
