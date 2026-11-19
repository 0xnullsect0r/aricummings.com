using System.Collections.Concurrent;

namespace Ari.Platform.Api;

/// <summary>
/// In-memory token bucket per key, thread-safe, with a maximum key count so an attacker
/// cannot fill memory. Limits reset on restart, which is fine.
/// </summary>
public sealed class RateLimiter(int capacity, double refillPerMinute)
{
    private sealed record Bucket(double Tokens, long LastRefillTicks);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private const int MaxKeys = 50_000;

    public bool TryConsume(string key)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        // AddOrUpdate always runs exactly one lambda, but the compiler cannot see that.
        var allowed = false;
        _buckets.AddOrUpdate(key,
            _ =>
            {
                allowed = capacity >= 1;
                return new Bucket(capacity - 1, now);
            },
            (_, existing) =>
            {
                var elapsedMinutes = (now - existing.LastRefillTicks) / (double)TimeSpan.TicksPerMinute;
                var available = Math.Min(capacity, existing.Tokens + elapsedMinutes * refillPerMinute);
                allowed = available >= 1;
                return new Bucket(Math.Max(-refillPerMinute, available - 1), now);
            });

        if (_buckets.Count > MaxKeys)
        {
            // Drop the coldest half rather than grow without bound.
            foreach (var stale in _buckets.Where(kvp => kvp.Value.LastRefillTicks < now - TimeSpan.TicksPerHour).Take(MaxKeys / 2).Select(kvp => kvp.Key).ToList())
                _buckets.TryRemove(stale, out _);
        }

        return allowed;
    }

    public TimeSpan? RetryAfter(string key)
    {
        if (!_buckets.TryGetValue(key, out var bucket)) return null;
        var deficit = -bucket.Tokens;
        if (deficit <= 0) return null;
        var seconds = (int)Math.Ceiling(deficit * 60.0 / Math.Max(1, refillPerMinute));
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }
}

/// <summary>Shared registry so routes grab named limiters and tests can disable them.</summary>
public sealed class RateLimits(bool disabled)
{
    public RateLimiter Contact { get; } = new(5, 1);
    public RateLimiter Beacon { get; } = new(120, 60);
    public RateLimiter SignInNetwork { get; } = new(20, 10);
    public RateLimiter SignInEmail { get; } = new(10, 2);
    public RateLimiter Reset { get; } = new(3, 0.5);
    public RateLimiter Refresh { get; } = new(30, 10);

    public bool TryConsume(RateLimiter limiter, string key) => disabled || limiter.TryConsume(key);
}
