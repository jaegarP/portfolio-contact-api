using System.Text;
using System.Text.Json;
using ContactApi.Core;
using ContactApi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactApi.Tests;

/// <summary>Tests the full request flow with in-memory fakes — no Azure or AWS needed.</summary>
public class ContactServiceTests
{
    private const string Site = "https://jaegarp.github.io";

    private sealed class FakeStore : IMessageStore
    {
        public List<(ContactSubmission Submission, string IpHash)> Saved { get; } = [];
        public Task<int> CountRecentAsync(string ipHash, TimeSpan window, CancellationToken ct) =>
            Task.FromResult(Saved.Count(s => s.IpHash == ipHash));
        public Task<string> SaveAsync(ContactSubmission s, string ipHash, string? ua, CancellationToken ct)
        {
            Saved.Add((s, ipHash));
            return Task.FromResult(Guid.NewGuid().ToString());
        }
    }

    private sealed class FakeEmail(bool fail = false) : IEmailNotifier
    {
        public List<ContactSubmission> Sent { get; } = [];
        public Task SendAsync(ContactSubmission s, CancellationToken ct)
        {
            if (fail) throw new InvalidOperationException("email down");
            Sent.Add(s);
            return Task.CompletedTask;
        }
    }

    private static (ContactService Service, FakeStore Store, FakeEmail Email) Create(bool emailFails = false, int max = 5)
    {
        var store = new FakeStore();
        var email = new FakeEmail(emailFails);
        var options = new ContactOptions { AllowedOrigins = [Site], MaxMessagesPerHour = max, IpHashSalt = "test" };
        return (new ContactService(store, email, options, NullLogger<ContactService>.Instance), store, email);
    }

    private static IncomingContact Request(object body, string? origin = Site, string ip = "203.0.113.7")
    {
        var json = body as string ?? JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);
        return new IncomingContact(origin, ip, "xunit", bytes.Length, new MemoryStream(bytes));
    }

    private static readonly object Valid = new { name = "Ada", email = "ada@example.com", message = "Hello, I'd like to talk." };

    [Fact]
    public async Task Valid_message_is_stored_and_emailed()
    {
        var (svc, store, email) = Create();
        var result = await svc.HandleAsync(Request(Valid), default);

        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Saved);
        Assert.Single(email.Sent);
        Assert.Equal("Ada", email.Sent[0].Name);
    }

    [Fact]
    public async Task Raw_ip_is_never_stored()
    {
        var (svc, store, _) = Create();
        await svc.HandleAsync(Request(Valid, ip: "203.0.113.7"), default);
        Assert.DoesNotContain("203.0.113.7", store.Saved[0].IpHash);
        Assert.Equal(32, store.Saved[0].IpHash.Length);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData(null)]
    public async Task Other_origins_are_rejected(string? origin)
    {
        var (svc, store, _) = Create();
        var result = await svc.HandleAsync(Request(Valid, origin), default);
        Assert.Equal(403, result.StatusCode);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Origin_with_trailing_slash_is_allowed()
    {
        var (svc, _, _) = Create();
        Assert.Equal(200, (await svc.HandleAsync(Request(Valid, Site + "/"), default)).StatusCode);
    }

    [Fact]
    public async Task Invalid_fields_return_400_with_field_errors()
    {
        var (svc, store, _) = Create();
        var result = await svc.HandleAsync(Request(new { name = "", email = "bad", message = "hi" }), default);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("\"email\"", JsonSerializer.Serialize(result.Body));
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Malformed_json_returns_400()
    {
        var (svc, _, _) = Create();
        Assert.Equal(400, (await svc.HandleAsync(Request("not json"), default)).StatusCode);
    }

    [Fact]
    public async Task Oversized_body_returns_413()
    {
        var (svc, _, _) = Create();
        // Valid JSON whose message alone is bigger than the limit, sent without a Content-Length header
        var huge = JsonSerializer.Serialize(new
        {
            name = "Ada", email = "ada@example.com", message = new string('x', (int)ContactService.MaxBodyBytes * 2),
        });
        var bytes = Encoding.UTF8.GetBytes(huge);
        var noLength = new IncomingContact(Site, "1.1.1.1", null, ContentLength: null, new MemoryStream(bytes));

        Assert.Equal(413, (await svc.HandleAsync(noLength, default)).StatusCode);
    }

    [Fact]
    public async Task Honeypot_pretends_success_but_stores_nothing()
    {
        var (svc, store, email) = Create();
        var result = await svc.HandleAsync(Request(new
        {
            name = "Bot", email = "bot@spam.com", message = "buy now buy now", website = "http://spam",
        }), default);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(store.Saved);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Rate_limit_blocks_after_max_messages_per_visitor()
    {
        var (svc, store, _) = Create(max: 2);
        Assert.Equal(200, (await svc.HandleAsync(Request(Valid), default)).StatusCode);
        Assert.Equal(200, (await svc.HandleAsync(Request(Valid), default)).StatusCode);
        Assert.Equal(429, (await svc.HandleAsync(Request(Valid), default)).StatusCode);
        // a different visitor is unaffected
        Assert.Equal(200, (await svc.HandleAsync(Request(Valid, ip: "198.51.100.1"), default)).StatusCode);
        Assert.Equal(3, store.Saved.Count);
    }

    [Fact]
    public async Task Email_failure_still_succeeds_because_message_is_stored()
    {
        var (svc, store, _) = Create(emailFails: true);
        var result = await svc.HandleAsync(Request(Valid), default);
        Assert.Equal(200, result.StatusCode);
        Assert.Single(store.Saved);
    }

    [Theory]
    [InlineData("203.0.113.7, 10.0.0.1", null, "203.0.113.7")]
    [InlineData("203.0.113.7:51234", null, "203.0.113.7")]
    [InlineData("2001:db8::1", null, "2001:db8::1")]
    [InlineData("", "192.0.2.5", "192.0.2.5")]
    [InlineData(null, null, null)]
    public void Client_ip_is_resolved_from_forwarded_header_or_socket(string? forwarded, string? remote, string? expected)
    {
        Assert.Equal(expected, ContactService.ResolveClientIp(forwarded, remote));
    }
}
