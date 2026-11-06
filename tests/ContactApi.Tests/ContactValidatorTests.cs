using ContactApi.Models;
using ContactApi.Services;

namespace ContactApi.Tests;

public class ContactValidatorTests
{
    private static ContactRequest Valid() => new()
    {
        Name = "Ada Lovelace",
        Email = "ada@example.com",
        Message = "I'd like to discuss a financial modelling role.",
    };

    [Fact]
    public void Valid_request_produces_trimmed_submission()
    {
        var result = ContactValidator.Validate(Valid() with { Name = "  Ada Lovelace  ", Email = " ada@example.com " });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal("Ada Lovelace", result.Submission!.Name);
        Assert.Equal("ada@example.com", result.Submission.Email);
    }

    [Fact]
    public void Null_body_is_rejected()
    {
        var result = ContactValidator.Validate(null);
        Assert.False(result.IsValid);
        Assert.Contains("body", result.Errors.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Missing_name_is_rejected(string? name)
    {
        var result = ContactValidator.Validate(Valid() with { Name = name });
        Assert.Contains("name", result.Errors.Keys);
    }

    [Fact]
    public void Overlong_name_is_rejected()
    {
        var result = ContactValidator.Validate(Valid() with { Name = new string('a', ContactValidator.NameMax + 1) });
        Assert.Contains("name", result.Errors.Keys);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a b@example.com")]
    [InlineData("Ada <ada@example.com>")]
    [InlineData("")]
    public void Invalid_email_is_rejected(string email)
    {
        var result = ContactValidator.Validate(Valid() with { Email = email });
        Assert.Contains("email", result.Errors.Keys);
    }

    [Theory]
    [InlineData("first.last@company.co.uk")]
    [InlineData("name+tag@gmail.com")]
    public void Common_email_formats_are_accepted(string email)
    {
        Assert.True(ContactValidator.Validate(Valid() with { Email = email }).IsValid);
    }

    [Fact]
    public void Too_short_message_is_rejected()
    {
        var result = ContactValidator.Validate(Valid() with { Message = "hi" });
        Assert.Contains("message", result.Errors.Keys);
    }

    [Fact]
    public void Too_long_message_is_rejected()
    {
        var result = ContactValidator.Validate(Valid() with { Message = new string('x', ContactValidator.MessageMax + 1) });
        Assert.Contains("message", result.Errors.Keys);
    }

    [Fact]
    public void Every_field_error_is_reported_at_once()
    {
        var result = ContactValidator.Validate(new ContactRequest());
        Assert.Equal(["email", "message", "name"], result.Errors.Keys.Order());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://spam.example", true)]
    public void Honeypot_flags_bots(string? website, bool expected)
    {
        Assert.Equal(expected, ContactValidator.IsLikelySpam(Valid() with { Website = website }));
    }
}
