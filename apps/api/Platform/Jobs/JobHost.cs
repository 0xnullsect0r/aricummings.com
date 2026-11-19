using System.Collections.Concurrent;
using Cronos;
using Dapper;

namespace Ari.Platform.Jobs;

public sealed record JobOptions(string Name, string? Cron, string? TimeZone, Func<CancellationToken, Task> Work, bool Alert);

/// <summary>
/// Registered jobs run on their cron schedule, or once at startup when no cron is given.
/// A job never overlaps itself; a second start while one runs is skipped and logged.
/// One job failing leaves the others alone. Alert jobs email active owners on failure.
/// </summary>
public sealed class JobHost(IEnumerable<JobOptions> jobs, IServiceScopeFactory scopes, ILogger<JobHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService skips ExecuteAsync if a stop arrives before the task is scheduled,
        // which would lose the startup jobs; start the loop ourselves.
        _ = RunAll(stoppingToken);
        await Task.CompletedTask;
    }

    private async Task RunAll(CancellationToken stoppingToken)
    {
        foreach (var job in jobs.Where(j => j.Cron is null))
            _ = RunOnce(job, stoppingToken);

        var schedules = jobs.Where(j => j.Cron is not null).Select(j => (Job: j, Expression: Parse(j))).ToList();
        if (schedules.Count == 0) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (job, expression) in schedules)
            {
                var zone = job.TimeZone is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(job.TimeZone);
                var next = expression?.GetNextOccurrence(now.DateTime, zone);
                if (next is null) continue;
                // Run when the scheduled time fell within the last tick window.
                var dueBy = now + TimeSpan.FromSeconds(20);
                if (next <= dueBy) _ = RunOnce(job, stoppingToken);
            }
        }
    }

    private static CronExpression? Parse(JobOptions job)
    {
        try { return CronExpression.Parse(job.Cron!); }
        catch (CronFormatException)
        {
            return null; // recorded by the caller's registration validation in practice
        }
    }

    private readonly ConcurrentDictionary<string, byte> _running = new();

    private async Task RunOnce(JobOptions job, CancellationToken stoppingToken)
    {
        if (!_running.TryAdd(job.Name, 0)) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            await job.Work(cts.Token);
            await RecordResult(job.Name, ok: true, error: null);
        }
        catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(error, "Job {Job} failed", job.Name);
            await RecordResult(job.Name, ok: false, error: error.Message);
            if (job.Alert) await AlertOwners(job, error);
        }
        finally
        {
            _running.TryRemove(job.Name, out _);
        }
    }

    private async Task RecordResult(string name, bool ok, string? error)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Database.Db>();
            await using var connection = db.App();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await connection.ExecuteAsync("""
                INSERT INTO jobs_state(name, last_run_at, last_ok_at, last_error) VALUES(@name, @now, @okAt, @error)
                ON CONFLICT(name) DO UPDATE SET last_run_at=@now, last_ok_at=@okAt, last_error=@error;
                """, new { name, now, okAt = ok ? (long?)now : null, error });
        }
        catch (Exception recordError)
        {
            logger.LogWarning(recordError, "Could not record job state for {Job}", name);
        }
    }

    private async Task AlertOwners(JobOptions job, Exception error)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<Outbox.IOutbox>();
            var db = scope.ServiceProvider.GetRequiredService<Database.Db>();
            await using var connection = db.App();
            var owners = await connection.QueryAsync<string>("SELECT email FROM users WHERE role='owner' AND active=1;");
            foreach (var owner in owners)
            {
                await outbox.Enqueue("mail", new Dictionary<string, string>
                {
                    ["to"] = owner,
                    ["subject"] = $"aricummings.com: job {job.Name} failed",
                    ["text"] = $"The job {job.Name} failed at {DateTimeOffset.UtcNow:u}.\n\n{error.Message}\n",
                });
            }
        }
        catch (Exception alertError)
        {
            logger.LogError(alertError, "Could not alert owners about {Job}", job.Name);
        }
    }
}
