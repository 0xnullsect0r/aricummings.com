using System.Text.Json;
using Ari.Platform.Database;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Ari.Platform.Outbox;

/// <summary>Anything that leaves the building goes through a table and a job.</summary>
public interface IOutbox
{
    Task Enqueue(string kind, IReadOnlyDictionary<string, string> payload);
}

public sealed class MailQueue(Db db) : IOutbox
{
    public async Task Enqueue(string kind, IReadOnlyDictionary<string, string> payload)
    {
        await using var connection = db.App();
        await connection.ExecuteAsync("""
            INSERT INTO outbox(kind, payload, next_attempt_at, created_at)
            VALUES(@kind, @payload, @now, @now);
            """,
            new { kind, payload = JsonSerializer.Serialize(payload), now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }
}

/// <summary>Implemented by Settings; the mail job refuses to run without a working configuration.</summary>
public interface ISmtpConfig
{
    (string Host, int Port, string User, string Password, string FromAddress, string FromName, bool StartTls)? Read();
}

public sealed class MailSender(ILogger<MailSender> logger)
{
    /// <summary>Sends one message. Timeouts on every outbound step.</summary>
    public async Task Send((string Host, int Port, string User, string Password, string FromAddress, string FromName, bool StartTls) config, MailPayload payload)
    {
        var message = new MimeMessage();
        var from = string.IsNullOrWhiteSpace(config.FromName)
            ? MailboxAddress.Parse(config.FromAddress)
            : new MailboxAddress(config.FromName, config.FromAddress);
        message.From.Add(from);
        message.To.Add(MailboxAddress.Parse(payload.To));
        if (!string.IsNullOrEmpty(payload.ReplyTo))
            message.ReplyTo.Add(MailboxAddress.Parse(payload.ReplyTo));
        // Line breaks never belong in a header the sender names.
        message.Subject = payload.Subject.ReplaceLineEndings(" ");

        var builder = new BodyBuilder { TextBody = payload.Text };
        if (!string.IsNullOrEmpty(payload.Html)) builder.HtmlBody = payload.Html;
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient { Timeout = 15_000 };
        await client.ConnectAsync(config.Host, config.Port, config.StartTls
            ? SecureSocketOptions.StartTls   // refuse plaintext when STARTTLS was promised
            : SecureSocketOptions.SslOnConnect);
        if (!string.IsNullOrEmpty(config.User))
            await client.AuthenticateAsync(config.User, config.Password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
        logger.LogInformation("Mail sent to {Recipient} subject '{Subject}'", payload.To, payload.Subject);
    }
}

public sealed record MailPayload(string To, string Subject, string Text, string? Html, string? ReplyTo);

/// <summary>
/// Every minute: a batch of 20 pending rows, attempts filtered in the query so dead rows
/// never block live mail. 8 attempts, backoff min(6h, 60s &lt;&lt; attempts).
/// </summary>
public sealed class OutboxJob(Db db, ISmtpConfig smtp, MailSender sender, ILogger<OutboxJob> logger)
{
    public async Task Run(CancellationToken cancellation)
    {
        var config = smtp.Read();
        if (config is null) return; // not configured: leave rows queued, never touch them

        await using var connection = db.App();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = (await connection.QueryAsync<(long Id, string Payload)>("""
            SELECT id, payload FROM outbox
            WHERE sent_at IS NULL AND attempts < 8 AND next_attempt_at <= @now
            ORDER BY next_attempt_at LIMIT 20;
            """, new { now })).ToList();

        foreach (var (id, payloadJson) in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            MailPayload payload;
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(payloadJson)
                    ?? throw new InvalidOperationException("empty payload");
                payload = new MailPayload(
                    map.GetValueOrDefault("to") ?? throw new InvalidOperationException("missing to"),
                    map.GetValueOrDefault("subject") ?? throw new InvalidOperationException("missing subject"),
                    map.GetValueOrDefault("text") ?? "",
                    map.GetValueOrDefault("html") is { Length: > 0 } html ? html : null,
                    map.GetValueOrDefault("reply_to") is { Length: > 0 } replyTo ? replyTo : null);
            }
            catch (JsonException error)
            {
                await MarkDead(connection, id, error.Message);
                continue;
            }

            try
            {
                await sender.Send(config.Value, payload);
                await connection.ExecuteAsync("UPDATE outbox SET sent_at=@now WHERE id=@id;", new { now, id });
            }
            catch (Exception error)
            {
                var attempts = await connection.ExecuteScalarAsync<int>("SELECT attempts FROM outbox WHERE id=@id;", new { id });
                attempts++;
                if (attempts >= 8)
                {
                    logger.LogError("Outbox row {Id} dead after {Attempts} attempts: {Error}", id, attempts, error.Message);
                    await MarkDead(connection, id, error.Message);
                }
                else
                {
                    var backoff = TimeSpan.FromSeconds(Math.Min(6 * 3600, 60L << attempts));
                    await connection.ExecuteAsync(
                        "UPDATE outbox SET attempts=@attempts, next_attempt_at=@next, last_error=@error WHERE id=@id;",
                        new { attempts, next = now + (long)backoff.TotalMilliseconds, error = error.Message[..Math.Min(error.Message.Length, 500)], id });
                }
            }
        }
    }

    private async Task MarkDead(Microsoft.Data.Sqlite.SqliteConnection connection, long id, string error) =>
        await connection.ExecuteAsync(
            "UPDATE outbox SET attempts=8, last_error=@error WHERE id=@id;",
            new { error = error[..Math.Min(error.Length, 500)], id });
}

/// <summary>Removes sent rows after 30 days, plus expired tokens, challenges and sessions.</summary>
public sealed class PruneJob(Db db, ILogger<PruneJob> logger)
{
    public async Task Run(CancellationToken cancellation)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        await using var app = db.App();
        await app.ExecuteAsync("DELETE FROM outbox WHERE sent_at IS NOT NULL AND sent_at < @cutoff;", new { cutoff });
        await app.ExecuteAsync("DELETE FROM auth_links WHERE expires_at < @now;", new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        await app.ExecuteAsync("DELETE FROM passkey_challenges WHERE expires_at < @now;", new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        await app.ExecuteAsync("DELETE FROM sessions WHERE expires_at < @now AND revoked_at IS NOT NULL;", new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        logger.LogInformation("Prune complete");
    }
}
