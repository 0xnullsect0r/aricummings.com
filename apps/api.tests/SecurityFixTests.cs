using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ari.Platform.Edge;
using Xunit;

namespace AriApi.Tests;

/// <summary>
/// Regression tests for the independent security review's findings: forwarded-IP
/// selection, chunked-body caps, lockout response parity.
/// </summary>
public sealed class SecurityFixTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await TestServer.Start("secfix", rateLimitsDisabled: true);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public void Forwarded_client_ip_takes_the_last_entry_not_the_first()
    {
        // A client can spoof every entry except the one the trusted proxy appends:
        // with one hop that is the last. (Review finding 1.)
        Assert.Equal("9.9.9.9", EdgeMiddleware.ClientIpFromForwarded("1.2.3.4, 9.9.9.9"));
        Assert.Equal("203.0.113.7", EdgeMiddleware.ClientIpFromForwarded("203.0.113.7"));
        Assert.Null(EdgeMiddleware.ClientIpFromForwarded("not an ip"));
        Assert.Null(EdgeMiddleware.ClientIpFromForwarded(""));
    }

    [Fact]
    public async Task Chunked_body_over_the_cap_is_refused_mid_stream()
    {
        // No Content-Length: the middleware's declared-size check cannot fire, so
        // the reader itself must cap. (Review finding 3.)
        var payload = new string('a', 5 * 1024 * 1024);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = null; // force chunked
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/signin") { Content = content };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60), BaseAddress = new Uri(_server.AdminOrigin) };
        using var response = await client.SendAsync(request);
        Assert.True(
            response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"expected 413/400, got {response.StatusCode}");
    }

    [Fact]
    public async Task Locked_account_answers_the_same_as_unknown_email()
    {
        // A distinct locked-out response is an enumeration oracle. (Review finding 8.)
        _server.CreateUser("locked-parity@example.com");
        var wrong = new StringContent(
            """{"email":"locked-parity@example.com","password":"a-wrong-password-123"}""",
            Encoding.UTF8, "application/json");
        for (var i = 0; i < 5; i++)
        {
            using var attempt = new HttpRequestMessage(HttpMethod.Post, "/api/auth/signin") { Content = wrong };
            await _server.Send("POST", "/api/auth/signin",
                """{"email":"locked-parity@example.com","password":"a-wrong-password-123"}""");
        }
        var (_, lockedBody, _) = await _server.Send("POST", "/api/auth/signin",
            """{"email":"locked-parity@example.com","password":"a-wrong-password-123"}""");
        var (_, unknownBody, _) = await _server.Send("POST", "/api/auth/signin",
            """{"email":"nobody-here@example.com","password":"a-wrong-password-123"}""");
        Assert.Equal(unknownBody, lockedBody);
    }
}
