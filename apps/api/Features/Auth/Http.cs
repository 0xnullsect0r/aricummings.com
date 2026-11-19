using System.Text;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Auth;

/// <summary>Small request helpers shared by the feature areas.</summary>
public static class HttpBody
{
    /// <summary>
    /// The raw JSON body, capped mid-stream: the middleware refuses a declared
    /// Content-Length over 1 MiB, but a chunked body has no declared length, so
    /// it is read through a bounded buffer and rejected the moment it crosses
    /// the cap instead of being materialized first.
    /// </summary>
    public static async Task<string> Read(HttpContext http)
    {
        var limit = 1_100_000;
        var buffer = new char[16 * 1024];
        var builder = new StringBuilder();
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) break;
            builder.Append(buffer, 0, read);
            if (builder.Length > limit) throw new ApiException(413, "Request body too large.", "body_too_large");
        }
        return builder.ToString();
    }
}

/// <summary>Audit shorthand: write path, no transaction (best-effort alongside the change).</summary>
public static class AuditExtensions
{
    public static void Audit(this SqliteConnection connection, long? actor, string action, string target, string detail, string? ip) =>
        AuditLog.Record(connection, null, actor, action, target, detail, ip);
}
