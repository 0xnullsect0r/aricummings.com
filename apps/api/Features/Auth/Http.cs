using System.Text;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Auth;

/// <summary>Small request helpers shared by the feature areas.</summary>
public static class HttpBody
{
    /// <summary>The raw JSON body. The middleware already refuses a Content-Length over
    /// 1 MiB; chunked bodies are capped here.</summary>
    public static async Task<string> Read(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        if (text.Length > 1_100_000) throw new ApiException(413, "Request body too large.", "body_too_large");
        return text;
    }
}

/// <summary>Audit shorthand: write path, no transaction (best-effort alongside the change).</summary>
public static class AuditExtensions
{
    public static void Audit(this SqliteConnection connection, long? actor, string action, string target, string detail, string? ip) =>
        AuditLog.Record(connection, null, actor, action, target, detail, ip);
}
