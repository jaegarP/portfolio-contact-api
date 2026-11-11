using System.Net;
using ContactApi.Core.Models;

namespace ContactApi.Core;

/// <summary>The notification email's wording, shared by every cloud's email sender.</summary>
public static class EmailTemplate
{
    public const string SiteName = "jaegarp.github.io";

    public static string Subject(ContactSubmission s) => $"Portfolio enquiry from {s.Name}";

    public static string PlainText(ContactSubmission s) =>
        $"{s.Message}\n\n— {s.Name} <{s.Email}>\nSent via {SiteName}";

    public static string Html(ContactSubmission s) => $"""
        <div style="font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#0f172a">
          <p style="white-space:pre-wrap">{WebUtility.HtmlEncode(s.Message)}</p>
          <hr style="border:none;border-top:1px solid #e2e8f0" />
          <p><strong>{WebUtility.HtmlEncode(s.Name)}</strong> &lt;{WebUtility.HtmlEncode(s.Email)}&gt;<br/>
          <span style="color:#64748b">Sent via your portfolio contact form — just hit Reply to answer.</span></p>
        </div>
        """;
}
