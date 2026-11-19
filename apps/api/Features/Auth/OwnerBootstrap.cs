using Ari.Platform;
using Ari.Platform.Database;
using Dapper;

namespace Ari.Features.Auth;

/// <summary>
/// CLI: "ari owner &lt;email&gt;" creates the first owner when none exists and prints a
/// one-time invite link (7 days). Once an owner exists, invites come from the admin.
/// </summary>
public sealed class OwnerBootstrap : ICliMode
{
    public string Name => "owner";

    public async Task<int> Run(Env env, string[] args)
    {
        var email = (args.Skip(1).FirstOrDefault() ?? Environment.GetEnvironmentVariable("ARI_OWNER_EMAIL") ?? "")
            .Trim().ToLowerInvariant();
        if (email.Length < 3 || !email.Contains('@') || email.Contains(' ') || email.Contains('\n') || email.Contains('\r'))
        {
            Console.Error.WriteLine("usage: ari owner <email>");
            return 2;
        }

        var db = new Db(env.DataDir);
        await using var connection = db.App();
        var existing = await connection.QuerySingleOrDefaultAsync<long?>("SELECT id FROM users WHERE role='owner' LIMIT 1;");
        if (existing is not null)
        {
            Console.Error.WriteLine("An owner already exists. New staff come from the admin's Users screen.");
            return 1;
        }

        var now = SessionService.Now();
        var userId = Db.Write(connection, transaction => connection.QuerySingle<long>(
            """
            INSERT INTO users(email, name, role, permissions, active, created_at)
            VALUES(@email, @name, 'owner', '[]', 1, @now)
            RETURNING id;
            """,
            new { email, name = email.Split('@')[0], now }, transaction));

        var link = await new LinkService(db).CreateInvite(userId, email, env.AdminOrigin);
        connection.Audit(userId, "users.owner_bootstrapped", email, "", null);
        Console.WriteLine("Owner invite. Valid for 7 days, shown once, stored only as a hash:");
        Console.WriteLine(link.Url);
        Console.WriteLine("Open it on " + env.AdminOrigin + " to set the owner name and password.");
        return 0;
    }
}
