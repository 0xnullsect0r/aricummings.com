using System.Security.Claims;
using System.Text;
using Ari.Platform.Crypto;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ari.Platform.Tokens;

public enum TokenAudience { Staff, Staff2Fa, Preview, StaffMarker }

/// <summary>
/// HS256 JWTs, one audience per purpose so a token of one kind can never pass as another.
/// Expiry is always required and clock skew is zero.
/// </summary>
public sealed class TokenIssuer(KeyRing keys)
{
    private const string Issuer = "ari";
    private const string Info = "site:jwt";

    private static string AudienceName(TokenAudience audience) => audience switch
    {
        TokenAudience.Staff => "staff",
        TokenAudience.Staff2Fa => "staff-2fa",
        TokenAudience.Preview => "preview",
        TokenAudience.StaffMarker => "staff-marker",
        _ => throw new ArgumentOutOfRangeException(nameof(audience)),
    };

    public string Issue(TokenAudience audience, TimeSpan lifetime, IEnumerable<(string Type, string Value)> claims)
    {
        var entry = keys.Current;
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = AudienceName(audience),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(keys.Derive(entry, Info)) { KeyId = entry.Kid },
                SecurityAlgorithms.HmacSha256),
            Claims = claims.ToDictionary(c => c.Type, c => (object)c.Value),
        };
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    public async Task<ClaimsPrincipal?> Read(string token, TokenAudience audience)
    {
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(token)) return null;
        var jwt = handler.ReadJsonWebToken(token);
        var kid = jwt.Kid;
        if (string.IsNullOrEmpty(kid)) return null;
        var entry = keys.Find(kid);
        if (entry is null) return null;
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = AudienceName(audience),
            IssuerSigningKey = new SymmetricSecurityKey(keys.Derive(entry, Info)) { KeyId = entry.Kid },
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };
        try
        {
            var result = await handler.ValidateTokenAsync(token, parameters);
            if (result.IsValid) return result.ClaimsIdentity is null ? null : new ClaimsPrincipal(result.ClaimsIdentity);
            return null;
        }
        catch (SecurityTokenException) { return null; }
        catch (ArgumentException) { return null; }
    }
}

public static class RandomToken
{
    /// <summary>32 random bytes as base64url. Only a SHA-256 of it is ever stored.</summary>
    public static string New() => B64.Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
