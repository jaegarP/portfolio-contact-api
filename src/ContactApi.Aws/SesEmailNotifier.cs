using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using ContactApi.Core;
using ContactApi.Core.Models;

namespace ContactApi.Aws;

/// <summary>
/// Sends the message with Amazon SES (v2 API).
/// "Reply" in your inbox goes straight to the visitor (Reply-To header).
/// Note: a new SES account starts in "sandbox" mode, where it can only send to verified
/// addresses — fine here, because it only ever emails you (your address is verified).
/// </summary>
public sealed class SesEmailNotifier(
    IAmazonSimpleEmailServiceV2 ses,
    string sender,
    string recipient,
    ILogger<SesEmailNotifier> logger) : IEmailNotifier
{
    public async Task SendAsync(ContactSubmission s, CancellationToken ct)
    {
        var response = await ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = sender,
            Destination = new Destination { ToAddresses = [recipient] },
            ReplyToAddresses = [s.Email],
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = EmailTemplate.Subject(s), Charset = "UTF-8" },
                    Body = new Body
                    {
                        Text = new Content { Data = EmailTemplate.PlainText(s), Charset = "UTF-8" },
                        Html = new Content { Data = EmailTemplate.Html(s), Charset = "UTF-8" },
                    },
                },
            },
        }, ct);
        logger.LogInformation("Email sent via SES, message id {MessageId}", response.MessageId);
    }
}
