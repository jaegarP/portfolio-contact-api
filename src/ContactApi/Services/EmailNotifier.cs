using Azure;
using Azure.Communication.Email;
using ContactApi.Core;
using ContactApi.Core.Models;
using Microsoft.Extensions.Logging;

namespace ContactApi.Services;

/// <summary>
/// Sends the message with Azure Communication Services Email.
/// "Reply" in your inbox goes straight to the visitor (Reply-To header).
/// </summary>
public sealed class AcsEmailNotifier(EmailClient client, string sender, string recipient, ILogger<AcsEmailNotifier> logger)
    : IEmailNotifier
{
    public async Task SendAsync(ContactSubmission s, CancellationToken ct)
    {
        var content = new EmailContent(EmailTemplate.Subject(s))
        {
            PlainText = EmailTemplate.PlainText(s),
            Html = EmailTemplate.Html(s),
        };
        var message = new EmailMessage(sender, recipient, content);
        message.ReplyTo.Add(new EmailAddress(s.Email, s.Name));

        // WaitUntil.Started: hand the email to Azure and return immediately, so the visitor isn't kept waiting.
        var op = await client.SendAsync(WaitUntil.Started, message, ct);
        logger.LogInformation("Email queued, operation {OperationId}", op.Id);
    }
}
