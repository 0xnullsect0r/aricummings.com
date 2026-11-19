using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AriApi.Tests;

/// <summary>
/// Cross-feature wiring: the auth session check sits in front of the content and
/// analytics admin routes (WiringFeature), anonymous is 401 so the SPA's refresh flow
/// engages, a signed-in owner passes, and staff without the permission get 403.
/// </summary>
public sealed class WiringTests : IAsyncLifetime
{
    private readonly TestServer _server = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<HttpStatusCode> SignIn(string email, TestServer.Jar jar)
    {
        var (status, _, _) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email, password = TestServer.DefaultPassword }), jar);
        return status;
    }

    [Fact]
    public async Task Anonymous_content_and_analytics_admin_routes_answer_401()
    {
        var (contentStatus, _, _) = await _server.Send("GET", "/api/admin/content");
        Assert.Equal(HttpStatusCode.Unauthorized, contentStatus);

        var (analyticsStatus, _, _) = await _server.Send("GET", "/api/admin/analytics/summary?days=7");
        Assert.Equal(HttpStatusCode.Unauthorized, analyticsStatus);
    }

    [Fact]
    public async Task Signed_in_owner_passes_both_guards()
    {
        _server.CreateUser("wiring-owner@example.com", role: "owner");
        var jar = new TestServer.Jar();
        Assert.Equal(HttpStatusCode.OK, await SignIn("wiring-owner@example.com", jar));

        var (contentStatus, _, _) = await _server.Send("GET", "/api/admin/content", null, jar);
        Assert.Equal(HttpStatusCode.OK, contentStatus);

        var (analyticsStatus, _, _) = await _server.Send("GET", "/api/admin/analytics/summary?days=7", null, jar);
        Assert.Equal(HttpStatusCode.OK, analyticsStatus);
    }

    [Fact]
    public async Task Staff_without_the_permission_get_403_not_401()
    {
        _server.CreateUser("wiring-staff@example.com",
            permissionsJson: """["messages.read"]""");
        var jar = new TestServer.Jar();
        Assert.Equal(HttpStatusCode.OK, await SignIn("wiring-staff@example.com", jar));

        var (contentStatus, _, _) = await _server.Send("GET", "/api/admin/content", null, jar);
        Assert.Equal(HttpStatusCode.Forbidden, contentStatus);
    }
}
