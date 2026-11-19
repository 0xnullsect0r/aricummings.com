using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Crypto;
using Xunit;

namespace AriApi.Tests;

public sealed class SealedTests
{
    private static Sealed NewSealed() => new(KeyRing.Load(null, $"k1:{Convert.ToBase64String(new byte[32])}", production: false));

    [Fact]
    public void Roundtrip_with_context()
    {
        var sealedValues = NewSealed();
        var value = sealedValues.EncryptString("smtp-password-here", "setting:smtp.password");
        Assert.StartsWith("v1.k1.", value);
        Assert.Equal("smtp-password-here", sealedValues.DecryptString(value, "setting:smtp.password"));
    }

    [Fact]
    public void Wrong_context_fails()
    {
        var sealedValues = NewSealed();
        var value = sealedValues.EncryptString("x", "setting:smtp.password");
        Assert.Null(sealedValues.DecryptString(value, "setting:smtp.host"));
    }

    [Fact]
    public void Tampered_body_fails()
    {
        var sealedValues = NewSealed();
        var value = sealedValues.EncryptString("x", "setting:a");
        var parts = value.Split('.');
        var body = B64.FromUrl(parts[4]);
        body[0] ^= 1;
        parts[4] = B64.Url(body);
        Assert.Null(sealedValues.DecryptString(string.Join('.', parts), "setting:a"));
    }
}

public sealed class RateLimiterTests
{
    [Fact]
    public void Denies_when_the_bucket_is_empty()
    {
        var limiter = new RateLimiter(capacity: 2, refillPerMinute: 0);
        Assert.True(limiter.TryConsume("k"));
        Assert.True(limiter.TryConsume("k"));
        Assert.False(limiter.TryConsume("k")); // exhausted: this was the backbone bug
        Assert.False(limiter.TryConsume("k"));
    }

    [Fact]
    public void Keys_are_independent()
    {
        var limiter = new RateLimiter(capacity: 1, refillPerMinute: 0);
        Assert.True(limiter.TryConsume("a"));
        Assert.False(limiter.TryConsume("a"));
        Assert.True(limiter.TryConsume("b"));
    }
}
