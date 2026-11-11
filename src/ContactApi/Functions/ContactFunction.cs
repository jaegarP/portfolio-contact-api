using ContactApi.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

namespace ContactApi.Functions;

/// <summary>
/// Azure Functions entry point: POST /api/contact.
/// Translates the HTTP request for the shared <see cref="ContactService"/> (ContactApi.Core),
/// which holds all the actual logic.
/// </summary>
public sealed class ContactFunction(ContactService service, ContactOptions options, IConfiguration config)
{
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
            if (!string.IsNullOrEmpty(origin) && options.IsAllowedOrigin(origin))
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

        var result = await service.HandleAsync(new IncomingContact(
            Origin: origin,
            ClientIp: ContactService.ResolveClientIp(
                req.Headers["X-Forwarded-For"].ToString(), req.HttpContext.Connection.RemoteIpAddress?.ToString()),
            UserAgent: req.Headers.UserAgent.ToString(),
            ContentLength: req.ContentLength,
            Body: req.Body), ct);

        return new ObjectResult(result.Body) { StatusCode = result.StatusCode };
    }
}
