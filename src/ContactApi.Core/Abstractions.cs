using ContactApi.Core.Models;
using Microsoft.Extensions.Logging;

namespace ContactApi.Core;

/// <summary>
/// Saves submissions and answers "how many has this visitor sent recently?" (rate limiting).
/// Implementations: Azure Table Storage (ContactApi), Amazon DynamoDB (ContactApi.Aws).
/// </summary>
public interface IMessageStore
{
    Task<int> CountRecentAsync(string ipHash, TimeSpan window, CancellationToken ct);
    Task<string> SaveAsync(ContactSubmission submission, string ipHash, string? userAgent, CancellationToken ct);
}

/// <summary>
/// Forwards a submission to the site owner's inbox.
/// Implementations: Azure Communication Services (ContactApi), Amazon SES (ContactApi.Aws).
/// </summary>
public interface IEmailNotifier
{
    Task SendAsync(ContactSubmission submission, CancellationToken ct);
}

/// <summary>Used when no database is configured (quick local runs): stores nothing, never rate-limits.</summary>
public sealed class NullMessageStore(ILogger<NullMessageStore> logger) : IMessageStore
{
    public Task<int> CountRecentAsync(string ipHash, TimeSpan window, CancellationToken ct) => Task.FromResult(0);

    public Task<string> SaveAsync(ContactSubmission s, string ipHash, string? userAgent, CancellationToken ct)
    {
        logger.LogWarning("No message store configured: message from {Name} was not stored.", s.Name);
        return Task.FromResult("not-stored");
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
