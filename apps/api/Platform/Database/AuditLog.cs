using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Platform.Database;

/// <summary>
/// Dotted actions: auth.login, content.published, media.deleted, settings.smtp_updated.
/// Never a secret, password or token in the detail. The pruner never touches this table.
/// </summary>
public static class AuditLog
{
    public static void Record(SqliteConnection connection, SqliteTransaction? transaction, long? actor, string action, string target, string detail, string? ip)
    {
        connection.Execute("""
            INSERT INTO audit_log(at, actor, action, target, detail, ip)
            VALUES (@at, @actor, @action, @target, @detail, @ip);
            """,
            new { at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), actor, action, target, detail, ip },
            transaction);
    }
}
