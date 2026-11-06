using System.Net;
using Azure;
using Azure.Communication.Email;
using ContactApi.Models;
using Microsoft.Extensions.Logging;

namespace ContactApi.Services;

/// <summary>Forwards a submission to the site owner's inbox.</summary>
public interface IEmailNotifier
{
    Task SendAsync(ContactSubmission submission, CancellationToken ct);
}

/// <summary>
/// Sends the message with Azure Communication Services Email.
/// "Reply" in your inbox goes straight to the visitor (Reply-To header).
/// </summary>
public sealed class AcsEmailNotifier(EmailClient client, string sender, string recipient, ILogger<AcsEmailNotifier> logger)
    : IEmailNotifier
{
    public async Task SendAsync(ContactSubmission s, CancellationToken ct)
    {
        var subject = $"Portfolio enquiry from {s.Name}";
        var plain = $"{s.Message}\n\n— {s.Name} <{s.Email}>\nSent via jaegarp.github.io";
        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#0f172a">
              <p style="white-space:pre-wrap">{WebUtility.HtmlEncode(s.Message)}</p>
              <hr style="border:none;border-top:1px solid #e2e8f0" />
              <p><strong>{WebUtility.HtmlEncode(s.Name)}</strong> &lt;{WebUtility.HtmlEncode(s.Email)}&gt;<br/>
              <span style="color:#64748b">Sent via your portfolio contact form — just hit Reply to answer.</span></p>
            </div>
            """;

        var message = new EmailMessage(sender, recipient, new EmailContent(subject) { PlainText = plain, Html = html });
        message.ReplyTo.Add(new EmailAddress(s.Email, s.Name));

        // WaitUntil.Started: hand the email to Azure and return immediately, so the visitor isn't kept waiting.
        var op = await client.SendAsync(WaitUntil.Started, message, ct);
        logger.LogInformation("Email queued, operation {OperationId}", op.Id);
    }
}

/// <summary>Used when email is not configured: logs instead of sending.</summary>
public sealed class LogOnlyEmailNotifier(ILogger<LogOnlyEmailNotifier> logger) : IEmailNotifier
{
    public Task SendAsync(ContactSubmission s, CancellationToken ct)
    {
        logger.LogWarning("Email not configured — would have sent message from {Name} <{Email}>.", s.Name, s.Email);
        return Task.CompletedTask;
    }
}
