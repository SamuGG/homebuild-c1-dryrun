using System.Globalization;
using System.Net;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using HomeBuild.Domain;

namespace HomeBuild.Notifications.Lambda;

/// <summary>An email to send: plain text plus a simple HTML version.</summary>
public sealed record EmailMessage(string To, string Subject, string Text, string Html);

/// <summary>Port: sends an email and returns the provider's message ID (SES in the function, in memory in tests).</summary>
public interface IEmailSender
{
    Task<string> SendAsync(EmailMessage email, CancellationToken cancellationToken);
}

/// <summary>SES (API v2) from a verified sender. In the SES sandbox the recipient must be verified too.</summary>
public sealed class SesEmailSender(IAmazonSimpleEmailServiceV2 ses, string from) : IEmailSender
{
    public async Task<string> SendAsync(EmailMessage email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        var response = await ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = from,
            Destination = new Destination { ToAddresses = [email.To] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = email.Subject, Charset = "UTF-8" },
                    Body = new Body { Text = new Content { Data = email.Text, Charset = "UTF-8" }, Html = new Content { Data = email.Html, Charset = "UTF-8" } },
                },
            },
        }, cancellationToken);
        return response.MessageId;
    }
}

/// <summary>
/// C10 Lab 6: where each reminder goes. Aftercare's reminders go to its team address. The buyer's would go to the buyer's
/// address; in the SES sandbox only verified addresses receive mail, so it is configuration here (BUYER_EMAIL), not a lookup.
/// </summary>
public sealed record EmailRouting(string? Aftercare, string? Buyer)
{
    public string? For(string kind) => kind switch
    {
        SnagReminderMessage.AcknowledgementOverdue => Aftercare,
        SnagReminderMessage.VisitTomorrow => Buyer,
        _ => null,
    };
}

/// <summary>The two reminder templates, plain text and simple HTML. Snag text is HTML-encoded; no names or addresses.</summary>
public static class SnagEmailTemplates
{
    public static EmailMessage For(SnagReminderMessage m, string to)
    {
        ArgumentNullException.ThrowIfNull(m);
        var (subject, lines) = m.Kind switch
        {
            SnagReminderMessage.AcknowledgementOverdue => ("Snag waiting for acknowledgement",
                new[] { "A snag reported by a buyer has not been acknowledged within two working days.", $"Where: {m.Area}", $"What: {m.Description}", "Please acknowledge it in HomeBuild Aftercare." }),
            SnagReminderMessage.VisitTomorrow => ("Your snag visit is tomorrow",
                new[] { string.Create(CultureInfo.InvariantCulture, $"{m.Subcontractor} will visit on {m.VisitOn:dddd d MMMM yyyy} to fix this snag:"), $"Where: {m.Area}", $"What: {m.Description}", "If you need to change the visit, reply in My Home." }),
            _ => throw new InvalidOperationException($"No email template for reminder kind '{m.Kind}'."),
        };
        var text = string.Join("\n\n", lines) + "\n\nHomeBuild\n";
        var html = "<html><body style=\"font-family:sans-serif\">" + string.Concat(lines.Select(l => $"<p>{WebUtility.HtmlEncode(l)}</p>")) + "<p>HomeBuild</p></body></html>";
        return new EmailMessage(to, subject, text, html);
    }
}
