using Ari.Platform.Database;
using Ari.Platform.Tokens;
using Dapper;

namespace Ari.Features.Auth;

/// <summary>
/// Invite and reset links. The link holds a random token; only its SHA-256 is stored,
/// so a database copy can't be used to sign in. Consume is a conditional update checked
/// for one row, so two requests can't both spend a link. Invites last 7 days, resets
/// 1 hour, and once one reset works the older ones in the same inbox stop working.
/// </summary>
public sealed class LinkService(Db db)
{
    public static readonly TimeSpan InviteLife = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResetLife = TimeSpan.FromHours(1);

    public sealed record IssuedLink(long UserId, string Email, string Token, string Url);

    public async Task<IssuedLink> CreateInvite(long userId, string email, string adminOrigin) =>
        await Create(db, userId, email, "invite", InviteLife, adminOrigin);

    public async Task<IssuedLink> CreateReset(long userId, string email, string adminOrigin) =>
        await Create(db, userId, email, "reset", ResetLife, adminOrigin);

    private static async Task<IssuedLink> Create(Db db, long userId, string email, string kind, TimeSpan life, string adminOrigin)
    {
        var token = RandomToken.New();
        var now = SessionService.Now();
        await using var connection = db.App();
        await connection.ExecuteAsync(
            "INSERT INTO auth_links(user_id, kind, email, token_hash, created_at, expires_at) VALUES(@userId, @kind, @email, @hash, @now, @expires);",
            new { userId, kind, email, hash = RandomToken.Hash(token), now, expires = now + (long)life.TotalMilliseconds });
        var page = kind == "invite" ? "/invited" : "/reset";
        return new IssuedLink(userId, email, token, adminOrigin.TrimEnd('/') + page + "?token=" + token);
    }

    /// <summary>Looks up a live, unused link without spending it, so request
    /// validation (password rules against the real email) can happen before Consume.</summary>
    public static async Task<(long UserId, string Email)?> Peek(Db db, string token, string kind)
    {
        await using var connection = db.App();
        return await connection.QuerySingleOrDefaultAsync<(long, string)?>(
            "SELECT user_id, email FROM auth_links WHERE token_hash=@hash AND kind=@kind AND used_at IS NULL AND expires_at > @now;",
            new { hash = RandomToken.Hash(token), kind, now = SessionService.Now() });
    }

    /// <summary>Spends the link if it is unused and unexpired, and returns the user it
    /// points at. A second spend returns null: the update matched no row.</summary>
    public static async Task<long?> Consume(Db db, string token, string kind)
    {
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var userId = await connection.ExecuteScalarAsync<long?>(
                """
                UPDATE auth_links SET used_at=@now
                WHERE token_hash=@hash AND kind=@kind AND used_at IS NULL AND expires_at > @now
                RETURNING user_id;
                """,
                new { now = SessionService.Now(), hash = RandomToken.Hash(token), kind }, transaction);
            if (userId is not null)
            {
                // One reset working kills every other outstanding link for that inbox.
                await connection.ExecuteAsync(
                    """
                    UPDATE auth_links SET used_at=@now
                    WHERE kind=@kind AND used_at IS NULL
                      AND email=(SELECT email FROM users WHERE id=@userId);
                    """,
                    new { now = SessionService.Now(), userId, kind }, transaction);
            }
            return userId;
        });
    }
}
