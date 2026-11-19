using System.Threading.Channels;
using Ari.Platform;
using Ari.Platform.Database;
using Ari.Platform.Edge;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Analytics;

/// <summary>
/// The one writer for analytics.db. Request paths only append to a channel and never
/// block or throw; a background loop drains every 2 seconds into a single transaction
/// on a connection only it holds. Past 5,000 buffered events everything is counted and
/// dropped; a failed batch gets one retry on a fresh connection and is then counted as
/// lost; if a drain still throws the loop restarts with a doubling delay up to a minute;
/// below 1 GB free on the volume recording stops and alerts once. Shutdown drains what
/// is left, from the hosted stop and again from a ShutdownHooks registration.
/// </summary>
public sealed class AnalyticsStore : BackgroundService, IPageHitCounter
{
    private const int BufferWeightCap = 5_000;
    private const long MinFreeBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    private abstract record Item(int Weight);
    private sealed record BeaconItem(IngestBeacon Beacon) : Item(Beacon.Weight);
    private sealed record BotItem(long At, string Ip, string? UserAgent, string Path) : Item(1);
    private sealed record HitItem(string Path) : Item(1);
    private sealed record DropItem(int Count) : Item(0);

    private readonly Db _db;
    private readonly ILogger<AnalyticsStore> _logger;
    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Lock _offerGate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _drainGate = new(1, 1); // one reader on the channel, always
    private readonly AnalyticsClock _clock;
    private int _bufferedWeight;
    private int _droppedWaiting;
    private int _pendingItems; // unbounded channels cannot report Count themselves
    private int _draining;
    private long _idleSince;
    private SqliteConnection? _writer;
    private bool _diskAlerted;

    public AnalyticsStore(Db db, AnalyticsClock clock, ILogger<AnalyticsStore> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// A page GET seen by the edge. Counted into daily totals before any filtering that
    /// follows (bots, journeys); only assets and non-page prefixes are skipped here.
    /// </summary>
    public void Count(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (path.StartsWith("/api/", StringComparison.Ordinal)
                || path.StartsWith("/media/", StringComparison.Ordinal)
                || path.StartsWith("/_next/", StringComparison.Ordinal)
                || path.StartsWith("/admin", StringComparison.Ordinal)
                || path == "/healthz") // the proxy's probe is not a page view
                return;
            var lastSegment = path.Split('/')[^1];
            if (lastSegment.Contains('.')) return; // a file, not a page
            Offer(new HitItem(path));
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Hit offer failed for {Path}", path);
        }
    }

    /// <summary>Never throws: over the buffer cap the beacon is counted and dropped.</summary>
    public void Offer(IngestBeacon beacon) => Offer(new BeaconItem(beacon));

    public void OfferBot(long at, string ip, string? userAgent, string path) => Offer(new BotItem(at, ip, userAgent, path));

    /// <summary>Drops that happen off the writer path (rate limit, oversized body) still count.</summary>
    public void CountDropped(int count = 1) => Offer(new DropItem(count));

    private void Offer(Item item)
    {
        lock (_offerGate)
        {
            if (_bufferedWeight + item.Weight > BufferWeightCap)
            {
                _droppedWaiting += Math.Max(item.Weight, 1);
                return;
            }
            _bufferedWeight += item.Weight;
        }
        if (!_channel.Writer.TryWrite(item))
        {
            lock (_offerGate) _bufferedWeight -= item.Weight;
            return;
        }
        Interlocked.Increment(ref _pendingItems);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService skips ExecuteAsync entirely when a stop arrives before the task
        // is scheduled, which would lose the buffered events; start the loop ourselves.
        _ = RunLoop(stoppingToken);
        await Task.CompletedTask;
    }

    private async Task RunLoop(CancellationToken stoppingToken)
    {
        ShutdownHooks.Register("analytics-drain", () => DrainNowAsync(TimeSpan.FromSeconds(5)));
        var backoff = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A drain that started must finish, or items leave the channel unwritten;
                // only the wait between drains observes cancellation.
                await DrainAsync(CancellationToken.None);
                backoff = TimeSpan.FromSeconds(2);
                await WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                // The loop died: alert, wait, restart. Queued events survive in the channel.
                _logger.LogError(error, "Analytics writer failed; restarting in {Backoff}", backoff);
                try { await Task.Delay(backoff, stoppingToken); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        Task wake;
        try { wake = _wake.WaitAsync(stoppingToken); }
        catch (ObjectDisposedException) { return; }
        var tick = Task.Delay(DrainInterval, stoppingToken);
        await Task.WhenAny(wake, tick);
    }

    /// <summary>Signals the loop and waits until the channel is drained and written, or the limit hits.</summary>
    public async Task<bool> FlushAsync(TimeSpan limit)
    {
        // One wake only: releasing on every poll would keep the loop draining and the
        // idle watermark would never age past the quiet window below.
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (Volatile.Read(ref _pendingItems) == 0 && Volatile.Read(ref _draining) == 0
                && Environment.TickCount64 - Volatile.Read(ref _idleSince) > 60)
                return true;
            await Task.Delay(20);
        }
        return false;
    }

    /// <summary>
    /// Drains directly, without the loop. For shutdown only: after the loop is cancelled
    /// nothing would answer a signal, so the stop path and the shutdown hook both drain
    /// what is left under their own deadline.
    /// </summary>
    private async Task<bool> DrainNowAsync(TimeSpan limit)
    {
        var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        try
        {
            // The channel has one reader: wait out any drain the loop is still finishing.
            while (Volatile.Read(ref _draining) != 0 && Environment.TickCount64 < deadline)
                await Task.Delay(20);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(100, deadline - Environment.TickCount64)));
            await DrainAsync(cts.Token);
            return true;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Analytics final drain failed");
            return false;
        }
    }

    private async Task DrainAsync(CancellationToken cancellation)
    {
        await _drainGate.WaitAsync(cancellation);
        try
        {
            await DrainLockedAsync(cancellation);
        }
        finally
        {
            _drainGate.Release();
        }
    }

    /// <summary>Runs under the drain gate: the channel has exactly one reader at a time.</summary>
    private async Task DrainLockedAsync(CancellationToken cancellation)
    {
        Interlocked.Increment(ref _draining);
        try
        {
            List<Item> batch = new();
            while (_channel.Reader.TryRead(out var item)) batch.Add(item);
            if (batch.Count > 0) Interlocked.Add(ref _pendingItems, -batch.Count);
            if (batch.Count == 0) return;

            if (DiskLow())
            {
                CountLost(batch, "disk guard: free space below 1 GB");
                return;
            }

            try
            {
                await WriteBatchAsync(batch, cancellation);
            }
            catch (Exception first)
            {
                _logger.LogError(first, "Analytics batch failed once; retrying on a fresh connection");
                DisposeWriter();
                try
                {
                    await WriteBatchAsync(batch, cancellation);
                }
                catch (Exception second)
                {
                    // One bad batch must not wedge the loop: count it lost and keep going.
                    _logger.LogError(second, "Analytics batch lost after retry");
                    DisposeWriter();
                    CountLost(batch, "batch lost after retry");
                }
            }
        }
        finally
        {
            Volatile.Write(ref _idleSince, Environment.TickCount64);
            Interlocked.Decrement(ref _draining);
        }
    }

    private void CountLost(List<Item> batch, string reason)
    {
        var lost = batch.Sum(i => Math.Max(i.Weight, 1));
        _logger.LogWarning("Dropping {Count} analytics events: {Reason}", lost, reason);
        lock (_offerGate) _droppedWaiting += lost;
        // A zero-weight marker so the next drain records the loss in ingest_stats.
        _channel.Writer.TryWrite(new DropItem(0));
    }

    private bool DiskLow()
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_db.AnalyticsPath)) ?? "/";
            var drive = new DriveInfo(directory);
            var low = drive.AvailableFreeSpace < MinFreeBytes;
            if (low && !_diskAlerted)
            {
                _logger.LogError("Analytics recording stopped: less than 1 GB free on {Directory}", directory);
                _diskAlerted = true;
            }
            else if (!low && _diskAlerted)
            {
                _logger.LogInformation("Analytics recording resumed: free space recovered on {Directory}", directory);
                _diskAlerted = false;
            }
            return low;
        }
        catch
        {
            return false; // cannot tell: keep recording
        }
    }

    private void DisposeWriter()
    {
        try { _writer?.Dispose(); }
        catch { }
        _writer = null;
    }

    /// <summary>The write connection, unpooled so a connection that hit an I/O error is really gone.</summary>
    private async Task<SqliteConnection> WriterAsync()
    {
        if (_writer is { State: System.Data.ConnectionState.Open }) return _writer;
        DisposeWriter();
        var connection = new SqliteConnection($"Data Source={_db.AnalyticsPath};Mode=ReadWriteCreate;Pooling=False");
        await connection.OpenAsync();
        foreach (var pragma in new[]
                 {
                     "PRAGMA journal_mode=WAL;",
                     "PRAGMA busy_timeout=5000;",
                     "PRAGMA synchronous=NORMAL;",
                     "PRAGMA journal_size_limit=67108864;",
                 })
            await connection.ExecuteAsync(pragma);
        _writer = connection;
        return connection;
    }

    private async Task WriteBatchAsync(List<Item> batch, CancellationToken cancellation)
    {
        var received = 0;
        var dropped = 0;
        var bots = 0;
        var hits = new Dictionary<string, int>();
        List<IngestBeacon> beacons = new();
        List<BotItem> botHits = new();

        foreach (var item in batch)
        {
            switch (item)
            {
                case BeaconItem beacon:
                    received += beacon.Weight;
                    beacons.Add(beacon.Beacon);
                    break;
                case BotItem bot:
                    received += 1;
                    bots += 1;
                    botHits.Add(bot);
                    break;
                case HitItem hit:
                    received += 1;
                    var day = _clock.Day(DateTimeOffset.UtcNow);
                    hits[day] = hits.GetValueOrDefault(day) + 1;
                    break;
                case DropItem drop:
                    dropped += drop.Count;
                    break;
            }
        }
        lock (_offerGate)
        {
            dropped += _droppedWaiting;
            _droppedWaiting = 0;
        }

        var connection = await WriterAsync();
        // The sync BeginTransaction is an in-process call; deferred: false takes the write
        // lock up front (BEGIN IMMEDIATE) so nothing interleaves with this batch.
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        cancellation.ThrowIfCancellationRequested();

        var statsDay = _clock.Day(DateTimeOffset.UtcNow);
        await connection.ExecuteAsync("""
            INSERT INTO ingest_stats(day, received, dropped, bots) VALUES(@day, @received, @dropped, @bots)
            ON CONFLICT(day) DO UPDATE SET
                received = received + @received,
                dropped = dropped + @dropped,
                bots = bots + @bots;
            """, new { day = statsDay, received, dropped, bots }, transaction);

        foreach (var (day, count) in hits)
        {
            await connection.ExecuteAsync("""
                INSERT INTO daily_totals(day, hits) VALUES(@day, @count)
                ON CONFLICT(day) DO UPDATE SET hits = hits + @count;
                """, new { day, count }, transaction);
        }

        foreach (var bot in botHits)
        {
            await connection.ExecuteAsync(
                "INSERT INTO bot_hits(at, ip, user_agent, path) VALUES(@At, @Ip, @UserAgent, @Path);",
                new { bot.At, bot.Ip, bot.UserAgent, bot.Path }, transaction);
        }

        foreach (var beacon in beacons)
            await WriteBeaconAsync(connection, transaction, beacon);

        await transaction.CommitAsync(cancellation);
        lock (_offerGate) _bufferedWeight -= batch.Sum(i => i.Weight);
    }

    private static async Task WriteBeaconAsync(SqliteConnection connection, SqliteTransaction transaction, IngestBeacon beacon)
    {
        string? lastPath = null;

        foreach (var e in beacon.Events)
        {
            switch (e.Type)
            {
                case "view":
                    await connection.ExecuteAsync("""
                        INSERT INTO visits(visitor, at, path, title, referrer, campaign, ip, ipv6_net, user_agent,
                                           browser, os, device, screen, language, timezone, country, region, city,
                                           network, session_id, bot, converted)
                        VALUES(@Visitor, @At, @Path, @Title, @Referrer, @Campaign, @Ip, @Ipv6Net, @UserAgent,
                                @Browser, @Os, @Device, @Screen, @Language, @Timezone, @Country, @Region, @City,
                                @Network, @Journey, 0, 0);
                        """,
                        new
                        {
                            beacon.Visitor,
                            beacon.At,
                            Path = e.Path ?? "(other)",
                            e.Title,
                            e.Referrer,
                            e.Campaign,
                            beacon.Ip,
                            beacon.Ipv6Net,
                            beacon.UserAgent,
                            beacon.Browser,
                            beacon.Os,
                            beacon.Device,
                            beacon.Screen,
                            beacon.Language,
                            beacon.Timezone,
                            Country = beacon.Geo?.Country,
                            Region = beacon.Geo?.Region,
                            City = beacon.Geo?.City,
                            Network = beacon.Geo?.Network,
                            beacon.Journey
                        }, transaction);
                    lastPath = e.Path;
                    break;

                case "active" when e.ActiveMs is { } ms:
                    await connection.ExecuteAsync("""
                        UPDATE visits SET active_ms = MAX(COALESCE(active_ms, 0), @ms)
                        WHERE id = (SELECT id FROM visits WHERE visitor = @Visitor AND session_id = @Journey
                                    ORDER BY at DESC, id DESC LIMIT 1);
                        """, new { ms, beacon.Visitor, beacon.Journey }, transaction);
                    break;

                case "scroll" when e.ScrollPct is { } pct:
                    await connection.ExecuteAsync("""
                        UPDATE visits SET scroll_pct = MAX(COALESCE(scroll_pct, 0), @pct)
                        WHERE id = (SELECT id FROM visits WHERE visitor = @Visitor AND session_id = @Journey
                                    ORDER BY at DESC, id DESC LIMIT 1);
                        """, new { pct, beacon.Visitor, beacon.Journey }, transaction);
                    break;

                case "click" or "outbound":
                    var clickPath = lastPath
                        ?? await connection.ExecuteScalarAsync<string?>(
                            "SELECT path FROM visits WHERE visitor = @Visitor AND session_id = @Journey ORDER BY at DESC, id DESC LIMIT 1;",
                            new { beacon.Visitor, beacon.Journey }, transaction);
                    await connection.ExecuteAsync("""
                        INSERT INTO click_events(visitor, at, path, label, target, x, y, section, kind, session)
                        VALUES(@Visitor, @At, @Path, @Label, @Target, @X, @Y, @Section, @Kind, @Journey);
                        """,
                        new
                        {
                            beacon.Visitor,
                            beacon.At,
                            Path = clickPath ?? "(other)",
                            Label = e.Label ?? e.Target ?? "(none)",
                            e.Target,
                            e.X,
                            e.Y,
                            e.Section,
                            Kind = e.Type,
                            beacon.Journey
                        }, transaction);
                    if (e.Type == "outbound")
                    {
                        // Outbound clicks are always conversions: mark the whole journey.
                        await connection.ExecuteAsync(
                            "UPDATE visits SET converted = 1 WHERE session_id = @Journey AND converted = 0;",
                            new { beacon.Journey }, transaction);
                    }
                    break;

                case "conversion":
                    var conversionPath = lastPath
                        ?? await connection.ExecuteScalarAsync<string?>(
                            "SELECT path FROM visits WHERE visitor = @Visitor AND session_id = @Journey ORDER BY at DESC, id DESC LIMIT 1;",
                            new { beacon.Visitor, beacon.Journey }, transaction);
                    await connection.ExecuteAsync("""
                        INSERT INTO click_events(visitor, at, path, label, target, x, y, section, kind, session)
                        VALUES(@Visitor, @At, @Path, @Label, NULL, NULL, NULL, NULL, 'conversion', @Journey);
                        """,
                        new { beacon.Visitor, beacon.At, Path = conversionPath ?? "(other)", Label = e.Label ?? "contact", beacon.Journey },
                        transaction);
                    await connection.ExecuteAsync(
                        "UPDATE visits SET converted = 1 WHERE session_id = @Journey AND converted = 0;",
                        new { beacon.Journey }, transaction);
                    break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancels the loop and waits for it, then drains what is left directly.
        await base.StopAsync(cancellationToken);
        await DrainNowAsync(TimeSpan.FromSeconds(5));
    }
}
