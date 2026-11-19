using System.Collections.Concurrent;

namespace Ari.Features.Auth;

/// <summary>
/// Correct token bucket, one per key, thread-safe, capped key count. Exists because
/// Platform's RateLimiter clamps its bucket at zero, which destroys the deficit and
/// makes TryConsume always succeed; same capacities, same keys, same
/// ARI_DISABLE_RATE_LIMITS flag. Fold back into Platform once that floor is removed.
/// </summary>
public sealed class TokenBucket(int capacity, double refillPerMinute)
{
    private sealed record State(double Tokens, long LastTicks);

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private const int MaxKeys = 50_000;

    public bool TryConsume(string key)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var state = _states.AddOrUpdate(key,
            _ => new State(capacity - 1, now),
            (_, existing) =>
            {
                var minutes = (now - existing.LastTicks) / (double)TimeSpan.TicksPerMinute;
                var tokens = Math.Min(capacity, existing.Tokens + minutes * refillPerMinute);
                return new State(tokens - 1, now);
            });

        if (_states.Count > MaxKeys)
        {
            foreach (var stale in _states.Where(kvp => kvp.Value.LastTicks < now - TimeSpan.TicksPerHour).Take(MaxKeys / 2).Select(kvp => kvp.Key).ToList())
                _states.TryRemove(stale, out _);
        }

        return state.Tokens >= 0;
    }

    public TimeSpan? RetryAfter(string key)
    {
        if (!_states.TryGetValue(key, out var state)) return null;
        var deficit = -state.Tokens;
        if (deficit <= 0) return null;
        var seconds = (int)Math.Ceiling(deficit * 60.0 / Math.Max(1, refillPerMinute));
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }
}

/// <summary>The site's buckets with the sizes that were pinned: sign-in per network
/// 20 with 10 a minute, per network+email 10 with 2, reset 3 with 0.5, refresh 30
/// with 10, contact 5 with 1. Tests disable them with the platform flag.</summary>
public sealed class RateGate
{
    public TokenBucket Contact { get; } = new(5, 1);
    public TokenBucket SignInNetwork { get; } = new(20, 10);
    public TokenBucket SignInEmail { get; } = new(10, 2);
    public TokenBucket Reset { get; } = new(3, 0.5);
    public TokenBucket Refresh { get; } = new(30, 10);

    public bool Disabled { get; }

    public RateGate(bool disabled) => Disabled = disabled;

    public static RateGate Load() => new(Environment.GetEnvironmentVariable("ARI_DISABLE_RATE_LIMITS") == "1");

    public bool TryConsume(TokenBucket bucket, string key) => Disabled || bucket.TryConsume(key);
}
