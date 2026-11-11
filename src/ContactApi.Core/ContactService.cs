using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContactApi.Core.Models;
using Microsoft.Extensions.Logging;

namespace ContactApi.Core;

/// <summary>Settings shared by every host (read from app settings / environment variables).</summary>
public sealed record ContactOptions
{
    /// <summary>Sites allowed to call the API. Empty = allow all (local development only).</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
    public int MaxMessagesPerHour { get; init; } = 5;
    public string IpHashSalt { get; init; } = "contact-api";

    public static ContactOptions From(Func<string, string?> setting) => new()
    {
        AllowedOrigins = (setting("AllowedOrigins") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        MaxMessagesPerHour = int.TryParse(setting("MaxMessagesPerHour"), out var max) ? max : 5,
        IpHashSalt = setting("IpHashSalt") is { Length: > 0 } salt ? salt : "contact-api",
    };

    public bool IsAllowedOrigin(string? origin)
    {
        if (AllowedOrigins.Count == 0) return true;         // not configured: allow all (local dev)
        if (string.IsNullOrEmpty(origin)) return false;     // configured: browsers always send Origin on POST
        return AllowedOrigins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Everything the service needs to know about one HTTP request, independent of the cloud.</summary>
public sealed record IncomingContact(
    string? Origin,
    string? ClientIp,
    string? UserAgent,
    long? ContentLength,
    Stream Body);

/// <summary>What to send back: an HTTP status code and a JSON-serialisable body.</summary>
public sealed record ContactResult(int StatusCode, object Body)
{
    public static ContactResult Ok() =>
        new(200, new { ok = true, message = "Thanks — your message has been sent. I'll be in touch soon." });

    public static ContactResult Error(int status, string message) => new(status, new { ok = false, error = message });
}

/// <summary>
/// The contact-form flow, shared by the Azure and AWS hosts:
/// origin check → size check → parse → honeypot → validate → rate limit → store → email.
/// </summary>
public sealed class ContactService(
    IMessageStore store,
    IEmailNotifier notifier,
    ContactOptions options,
    ILogger<ContactService> logger)
{
    public const long MaxBodyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ContactResult> HandleAsync(IncomingContact request, CancellationToken ct)
    {
        // 1. Only accept browser calls from the portfolio's own pages.
        if (!options.IsAllowedOrigin(request.Origin))
        {
            logger.LogWarning("Rejected request from origin {Origin}", request.Origin);
            return ContactResult.Error(403, "This origin is not allowed.");
        }

        // 2. Refuse oversized bodies before reading them.
        if (request.ContentLength is > MaxBodyBytes)
            return ContactResult.Error(413, "Message is too large.");

        // 3. Parse JSON (reading at most MaxBodyBytes, in case Content-Length was missing).
        ContactRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ContactRequest>(
                new LimitedStream(request.Body, MaxBodyBytes), Json, ct);
        }
        catch (JsonException)
        {
            return ContactResult.Error(400, "Request body must be JSON.");
        }
        catch (InvalidDataException)
        {
            return ContactResult.Error(413, "Message is too large.");
        }

        // 4. Honeypot: pretend success so bots don't learn they were caught.
        if (ContactValidator.IsLikelySpam(body))
        {
            logger.LogInformation("Honeypot triggered; dropping submission.");
            return ContactResult.Ok();
        }

        // 5. Validate fields.
        var result = ContactValidator.Validate(body);
        if (!result.IsValid)
            return new ContactResult(400, new { ok = false, errors = result.Errors });
        var submission = result.Submission!;

        // 6. Rate limit per visitor (IP is hashed — the raw address is never stored).
        var ipHash = HashIp(request.ClientIp ?? "unknown");
        if (await store.CountRecentAsync(ipHash, TimeSpan.FromHours(1), ct) >= options.MaxMessagesPerHour)
            return ContactResult.Error(429, "Too many messages — please try again later.");

        // 7. Store, then email. Storing first means a message is never lost if email fails.
        await store.SaveAsync(submission, ipHash, request.UserAgent, ct);
        try
        {
            await notifier.SendAsync(submission, ct);
        }
        catch (Exception ex)
        {
            // The message is safely stored, so the visitor still gets a success response.
            logger.LogError(ex, "Email notification failed; message is stored.");
        }

        return ContactResult.Ok();
    }

    /// <summary>
    /// The visitor's IP from X-Forwarded-For (set by Azure / AWS front ends) or the socket address.
    /// Strips a port from IPv4 "1.2.3.4:5678"; leaves IPv6 alone.
    /// </summary>
    public static string? ResolveClientIp(string? forwardedFor, string? remoteAddress)
    {
        var first = (forwardedFor ?? "").Split(',')[0].Trim();
        if (first.Length == 0) return remoteAddress;
        var colon = first.LastIndexOf(':');
        return colon > 0 && first.Count(c => c == ':') == 1 ? first[..colon] : first;
    }

    private string HashIp(string ip)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(options.IpHashSalt + ip));
        return Convert.ToHexString(bytes, 0, 16);
    }

    /// <summary>Read-only wrapper that throws once more than <c>max</c> bytes have been read.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));

        private int Count(int n)
        {
            _read += n;
            if (_read > max) throw new InvalidDataException("Body too large.");
            return n;
        }
    }
}
