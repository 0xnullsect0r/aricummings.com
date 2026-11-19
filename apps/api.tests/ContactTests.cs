using System.Net;
using System.Text.Json;

namespace AriApi.Tests;

/// <summary>
/// The public contact form on the public surface, with the platform rate limits left
/// ON: stores the message, enqueues the mail with Reply-To, swallows _gotcha bots,
/// and trips the 5-per-minute bucket.
/// </summary>
[Collection("AuthSerial")]
public sealed class ContactTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await TestServer.Start("contact", rateLimitsDisabled: false);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<(HttpStatusCode Status, string Body)> Submit(string json)
    {
        var (status, body, _) = await _server.Send("POST", "/api/contact", json, port: _server.PublicPort);
        return (status, body);
    }

    [Fact]
    public async Task A_real_message_is_stored_and_mailed_to_contact()
    {
        var (status, body) = await Submit(JsonSerializer.Serialize(new
        {
            name = " Interested Person ",
            email = "interested@example.com",
            body = "  Hello, I saw your portfolio.  ",
        }));
        Assert.Equal((int)HttpStatusCode.NoContent, (int)status);
        Assert.Empty(body);

        var message = _server.Query<(string Name, string Email, string Body)>(
            "SELECT name AS Name, email AS Email, body AS Body FROM messages LIMIT 1;");
        Assert.Equal("Interested Person", message.Name);
        Assert.Equal("interested@example.com", message.Email);
        Assert.Equal("Hello, I saw your portfolio.", message.Body.Trim());

        var mail = _server.Query<string>("SELECT payload FROM outbox WHERE kind='mail' LIMIT 1;");
        using var document = JsonDocument.Parse(mail);
        var payload = document.RootElement;
        Assert.Equal("contact@aricummings.com", payload.GetProperty("to").GetString());
        Assert.Equal("interested@example.com", payload.GetProperty("reply_to").GetString());
        Assert.Contains("Interested Person", payload.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task A_filled_gotcha_field_is_swallowed_silently()
    {
        var (status, body) = await Submit(JsonSerializer.Serialize(new
        {
            name = "Bot",
            email = "bot@example.com",
            body = "Buy my thing",
            _gotcha = "http://spam.example",
        }));
        Assert.Equal((int)HttpStatusCode.NoContent, (int)status);
        Assert.Equal(0, _server.Query<long>("SELECT COUNT(*) FROM messages;"));
        Assert.Equal(0, _server.Query<long>("SELECT COUNT(*) FROM outbox;"));
    }

    [Fact]
    public async Task Missing_fields_answer_field_errors()
    {
        var (status, body) = await Submit("""{"name":"Only A Name"}""");
        Assert.Equal((int)HttpStatusCode.BadRequest, (int)status);
        Assert.Contains("invalid_fields", body);
        Assert.Contains("email", body);
        Assert.Contains("body", body);
        Assert.Equal(0, _server.Query<long>("SELECT COUNT(*) FROM messages;"));
    }

    [Fact]
    public async Task Contact_is_admin_surface_invisible()
    {
        // The admin surface 404s everything it does not serve; /api/contact is public.
        var (status, _, _) = await _server.Send("POST", "/api/contact",
            JsonSerializer.Serialize(new { name = "P", email = "p@example.com", body = "b" }),
            port: _server.AdminPort);
        Assert.Equal((int)HttpStatusCode.NotFound, (int)status);
    }
}

/// <summary>Own server, own fresh rate buckets: nothing else may spend the contact
/// allowance before this test counts it out.</summary>
[Collection("AuthSerial")]
public sealed class ContactRateLimitTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await TestServer.Start("contact-rate", rateLimitsDisabled: false);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task The_burst_limit_is_five_per_minute()
    {
        var payload = JsonSerializer.Serialize(new { name = "P", email = "p@example.com", body = "b" });
        var succeeded = 0;
        var limited = false;
        for (var attempt = 0; attempt < 8 && !limited; attempt++)
        {
            var (status, body, _) = await _server.Send("POST", "/api/contact", payload, port: _server.PublicPort);
            if ((int)status == (int)HttpStatusCode.NoContent) succeeded++;
            else if ((int)status == 429 && body.Contains("rate_limited")) limited = true;
            else Assert.Fail($"Unexpected contact response {(int)status}: {body}");
        }
        Assert.True(succeeded >= 1, "at least one message should go through");
        Assert.True(limited, "the 5-per-minute bucket should trip within 8 attempts");
        // Only the first five (bucket capacity) can land; the rest were refused.
        var stored = _server.Query<long>("SELECT COUNT(*) FROM messages;");
        Assert.Equal(succeeded, stored);
    }
}
