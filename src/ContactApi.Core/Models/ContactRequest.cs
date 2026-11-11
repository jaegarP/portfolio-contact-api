namespace ContactApi.Core.Models;

/// <summary>What the website's contact form sends (JSON body).</summary>
public sealed record ContactRequest
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// Honeypot field. It is hidden on the website, so real visitors leave it empty;
    /// spam bots that fill in every field give themselves away.
    /// </summary>
    public string? Website { get; init; }
}

/// <summary>A validated, trimmed submission ready to store and forward.</summary>
public sealed record ContactSubmission(string Name, string Email, string Message);
