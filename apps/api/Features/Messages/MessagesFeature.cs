using Ari.Features.Auth;
using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Dapper;

namespace Ari.Features.Messages;

/// <summary>
/// The contact form: validate, store, mail the site owner with the sender as Reply-To.
/// Nothing goes back to the sender, so the form can't be used to send mail to others.
/// A hidden _gotcha field quietly swallows bot submissions, and the whole route is
/// limited to 5 a minute per network. The inbox routes are staff-only.
/// </summary>
public sealed class MessagesService(ApiContext api, RateGate limits, ILogger<MessagesService> logger)
{
    public const string ContactRecipient = "contact@aricummings.com";

    public async Task<IResult> Contact(HttpContext http)
    {
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("name", out var name, 1, 200);
        validator.Require("email", out var email, 3, 254);
        validator.Require("body", out var message, 1, 10_000);
        validator.Optional("_gotcha", out var gotcha, 500);
        validator.Done();

        // Line breaks never belong in a header the sender names.
        var replyTo = email.Trim().ReplaceLineEndings(" ");
        if (!replyTo.Contains('@') || replyTo.Any(char.IsControl) || replyTo.Any(char.IsWhiteSpace) || replyTo.Split('@').Length != 2)
            throw new ApiException(400, "Email is not valid.", "invalid_fields");
        name = name.Trim();

        var network = Requests.NetworkKey(http);
        if (!limits.TryConsume(limits.Contact, "contact:" + network))
        {
            if (limits.Contact.RetryAfter("contact:" + network) is { } wait)
                http.Response.Headers.RetryAfter = ((int)wait.TotalSeconds).ToString();
            return Results.Json(new { error = "Too many messages. Try again in a minute.", code = "rate_limited" }, statusCode: 429);
        }

        // A filled _gotcha means a bot: answer success, keep nothing.
        if (gotcha.Trim().Length > 0) return Results.NoContent();

        var userAgent = http.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 300) userAgent = userAgent[..300];
        var ip = Requests.ClientIp(http);

        await using var connection = api.Db.App();
        var id = Db.Write(connection, transaction => connection.QuerySingle<long>(
            """
            INSERT INTO messages(name, email, body, ip, user_agent, created_at)
            VALUES(@name, @email, @body, @ip, @userAgent, @now)
            RETURNING id;
            """,
            new { name, email = replyTo, body = message.Trim(), ip, userAgent, now = SessionService.Now() }, transaction));
        connection.Audit(null, "messages.contact", "#" + id, name, ip);

        await api.Queue.Enqueue("mail", new Dictionary<string, string>
        {
            ["to"] = ContactRecipient,
            ["reply_to"] = replyTo,
            ["subject"] = $"aricummings.com message from {name}",
            ["text"] = $"From: {name} <{replyTo}>\nIP: {ip}\n\n{message.Trim()}\n",
        });
        logger.LogInformation("Contact message {Id} stored", id);
        return Results.NoContent();
    }

    public async Task<IResult> Inbox(HttpContext http)
    {
        var box = http.Request.Query["box"].ToString();
        if (box.Length == 0) box = "inbox";
        if (box is not ("inbox" or "archive" or "all"))
            throw new ApiException(400, "box must be inbox, archive or all.", "invalid_fields");

        await using var connection = api.Db.App();
        var archivedFilter = box switch
        {
            "inbox" => "AND archived_at IS NULL",
            "archive" => "AND archived_at IS NOT NULL",
            _ => "",
        };
        var rows = await connection.QueryAsync<(long Id, string Name, string Email, string Body, long CreatedAt, long? ReadAt, long? ArchivedAt)>(
            $"""
             SELECT id AS Id, name AS Name, email AS Email, body AS Body, created_at AS CreatedAt, read_at AS ReadAt, archived_at AS ArchivedAt
             FROM messages WHERE 1=1 {archivedFilter}
             ORDER BY id DESC LIMIT 100;
             """);
        var unread = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM messages WHERE read_at IS NULL AND archived_at IS NULL;");
        return Results.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                email = r.Email,
                body = r.Body,
                createdAt = r.CreatedAt,
                read = r.ReadAt is not null,
                archived = r.ArchivedAt is not null,
            }),
            unread,
        });
    }

    public async Task<IResult> Update(HttpContext http, long id)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Optional("read", out var read, 10);
        validator.Optional("archived", out var archived, 10);
        validator.Done();
        if (read.Length > 0 && read is not ("true" or "false"))
            throw new ApiException(400, "read must be true or false.", "invalid_fields");
        if (archived.Length > 0 && archived is not ("true" or "false"))
            throw new ApiException(400, "archived must be true or false.", "invalid_fields");
        if (read.Length == 0 && archived.Length == 0)
            throw new ApiException(400, "Nothing to change.", "invalid_fields");

        var now = SessionService.Now();
        await using var connection = api.Db.App();
        if (read.Length > 0)
            await connection.ExecuteAsync("UPDATE messages SET read_at=@at WHERE id=@id AND read_at IS NULL;", new { at = now, id });
        if (archived.Length > 0)
            await connection.ExecuteAsync(
                "UPDATE messages SET archived_at=@at WHERE id=@id;", new { at = archived == "true" ? (long?)now : null, id });
        var touched = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM messages WHERE id=@id;", new { id });
        if (touched == 0) throw new ApiException(404, "No such message.", "not_found");
        connection.Audit(staff.UserId, "messages.updated", "#" + id,
            $"read={read} archived={archived}", Requests.ClientIp(http));
        return Results.NoContent();
    }

    public async Task<IResult> Delete(HttpContext http, long id)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        await using var connection = api.Db.App();
        var deleted = await connection.ExecuteAsync("DELETE FROM messages WHERE id=@id;", new { id });
        if (deleted != 1) throw new ApiException(404, "No such message.", "not_found");
        connection.Audit(staff.UserId, "messages.deleted", "#" + id, "", Requests.ClientIp(http));
        return Results.NoContent();
    }
}

public sealed class MessagesFeature : IFeature
{
    public int Order => 60;

    public void Register(IServiceCollection services) => services.AddSingleton<MessagesService>();

    public void Map(WebApplication app)
    {
        // Public contact form. The public surface only allows this one /api route;
        // the admin surface 404s it, so the staff inbox lives on /api/admin/messages.
        app.MapPost("/api/contact", async (HttpContext http, MessagesService service) => await service.Contact(http));

        app.MapGet("/api/admin/messages", async (HttpContext http, MessagesService service) => await service.Inbox(http))
            .AddEndpointFilter(Guards.RequirePermission("messages.read"));
        app.MapPatch("/api/admin/messages/{id}", async (HttpContext http, long id, MessagesService service) => await service.Update(http, id))
            .AddEndpointFilter(Guards.RequirePermission("messages.manage"));
        app.MapDelete("/api/admin/messages/{id}", async (HttpContext http, long id, MessagesService service) => await service.Delete(http, id))
            .AddEndpointFilter(Guards.RequirePermission("messages.manage"));
    }
}
