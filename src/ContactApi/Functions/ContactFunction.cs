using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContactApi.Models;
using ContactApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContactApi.Functions;

/// <summary>
/// POST /api/contact — receives the portfolio contact form.
/// Flow: origin check → size check → parse → honeypot → validate → rate limit → store → email → 200.
/// </summary>
public sealed class ContactFunction(
    IMessageStore store,
    IEmailNotifier notifier,
    IConfiguration config,
    ILogger<ContactFunction> logger)
{
    private const long MaxBodyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function("contact")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "contact")] HttpRequest req,
        CancellationToken ct)
    {
        var origin = req.Headers.Origin.ToString();

        // CORS: on Azure the platform handles it (EnableCors=false). In Docker there is no platform,
        // so EnableCors=true makes the function answer the browser's preflight and add the headers itself.
        if (config.GetValue("EnableCors", false))
        {
            if (!string.IsNullOrEmpty(origin) && IsAllowedOrigin(origin))
            {
                var h = req.HttpContext.Response.Headers;
                h.AccessControlAllowOrigin = origin;
                h.AccessControlAllowMethods = "POST, OPTIONS";
                h.AccessControlAllowHeaders = "Content-Type";
                h.AccessControlMaxAge = "3600";
                h.Vary = "Origin";
            }
            if (HttpMethods.IsOptions(req.Method)) return new NoContentResult();
        }

        // 1. Only accept browser calls from the portfolio's own pages.
        //    (Browsers also enforce this via CORS; this stops casual misuse from other sites.)
        if (!IsAllowedOrigin(origin))
        {
            logger.LogWarning("Rejected request from origin {Origin}", origin);
            return Problem(StatusCodes.Status403Forbidden, "This origin is not allowed.");
        }

        // 2. Refuse oversized bodies before reading them.
        if (req.ContentLength is > MaxBodyBytes)
            return Problem(StatusCodes.Status413PayloadTooLarge, "Message is too large.");

        // 3. Parse JSON.
        ContactRequest? body;
        try
        {
            body = await req.ReadFromJsonAsync<ContactRequest>(Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Problem(StatusCodes.Status400BadRequest, "Request body must be JSON.");
        }

        // 4. Honeypot: pretend success so bots don't learn they were caught.
        if (ContactValidator.IsLikelySpam(body))
        {
            logger.LogInformation("Honeypot triggered; dropping submission.");
            return Ok();
        }

        // 5. Validate fields.
        var result = ContactValidator.Validate(body);
        if (!result.IsValid)
            return new BadRequestObjectResult(new { ok = false, errors = result.Errors });

        var submission = result.Submission!;

        // 6. Rate limit per visitor (IP is hashed — the raw address is never stored).
        var ipHash = HashIp(ClientIp(req));
        var maxPerHour = config.GetValue("MaxMessagesPerHour", 5);
        if (await store.CountRecentAsync(ipHash, TimeSpan.FromHours(1), ct) >= maxPerHour)
            return Problem(StatusCodes.Status429TooManyRequests, "Too many messages — please try again later.");

        // 7. Store, then email. Storing first means a message is never lost if email fails.
        await store.SaveAsync(submission, ipHash, req.Headers.UserAgent.ToString(), ct);
        try
        {
            await notifier.SendAsync(submission, ct);
        }
        catch (Exception ex)
        {
            // The message is safely stored, so the visitor still gets a success response.
            logger.LogError(ex, "Email notification failed; message is stored in Table Storage.");
        }

        return Ok();
    }

    private static OkObjectResult Ok() =>
        new(new { ok = true, message = "Thanks — your message has been sent. I'll be in touch soon." });

    private static ObjectResult Problem(int status, string message) =>
        new(new { ok = false, error = message }) { StatusCode = status };

    private bool IsAllowedOrigin(string origin)
    {
        var allowed = (config["AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length == 0) return true;          // not configured: allow all (local dev)
        if (string.IsNullOrEmpty(origin)) return false; // configured: browsers always send Origin on POST
        return allowed.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
    }

    private static string ClientIp(HttpRequest req)
    {
        // Azure's front end puts the real client address first in X-Forwarded-For ("ip:port" or "ip").
        var forwarded = req.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
        if (forwarded.Length > 0)
        {
            var lastColon = forwarded.LastIndexOf(':');
            // strip a port from IPv4 "1.2.3.4:5678" but leave IPv6 addresses alone
            return lastColon > 0 && forwarded.Count(c => c == ':') == 1 ? forwarded[..lastColon] : forwarded;
        }
        return req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private string HashIp(string ip)
    {
        var salt = config["IpHashSalt"] ?? "contact-api";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + ip));
        return Convert.ToHexString(bytes, 0, 16);
    }
}
