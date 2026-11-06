using System.Net.Mail;
using ContactApi.Models;

namespace ContactApi.Services;

/// <summary>Checks and cleans a contact-form submission. Pure logic, so it is easy to unit test.</summary>
public static class ContactValidator
{
    public const int NameMax = 100;
    public const int EmailMax = 254;
    public const int MessageMin = 10;
    public const int MessageMax = 5000;

    public static ValidationResult Validate(ContactRequest? request)
    {
        var errors = new Dictionary<string, string>();
        if (request is null)
        {
            errors["body"] = "Request body is missing.";
            return new ValidationResult(null, errors);
        }

        var name = (request.Name ?? "").Trim();
        var email = (request.Email ?? "").Trim();
        var message = (request.Message ?? "").Trim();

        if (name.Length == 0) errors["name"] = "Please enter your name.";
        else if (name.Length > NameMax) errors["name"] = $"Name must be {NameMax} characters or fewer.";

        if (email.Length == 0) errors["email"] = "Please enter your email address.";
        else if (email.Length > EmailMax || !IsValidEmail(email)) errors["email"] = "Please enter a valid email address.";

        if (message.Length < MessageMin) errors["message"] = $"Message must be at least {MessageMin} characters.";
        else if (message.Length > MessageMax) errors["message"] = $"Message must be {MessageMax} characters or fewer.";

        return errors.Count == 0
            ? new ValidationResult(new ContactSubmission(name, email, message), errors)
            : new ValidationResult(null, errors);
    }

    /// <summary>True when the hidden honeypot field was filled in (almost certainly a bot).</summary>
    public static bool IsLikelySpam(ContactRequest? request) => !string.IsNullOrWhiteSpace(request?.Website);

    private static bool IsValidEmail(string email) =>
        MailAddress.TryCreate(email, out var parsed)
        && parsed.Address.Equals(email, StringComparison.OrdinalIgnoreCase)
        && parsed.Host.Contains('.')
        && !email.Any(char.IsWhiteSpace);
}

public sealed record ValidationResult(ContactSubmission? Submission, IReadOnlyDictionary<string, string> Errors)
{
    public bool IsValid => Submission is not null;
}
