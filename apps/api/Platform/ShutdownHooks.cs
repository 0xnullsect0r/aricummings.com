using Microsoft.Extensions.Hosting;

namespace Ari.Platform;

/// <summary>
/// Registered hooks run after requests drain, newest first. Register the database close
/// before the analytics drain so the drain runs first (registration order, stop in reverse).
/// </summary>
public static class ShutdownHooks
{
    private static readonly List<(string Name, Func<Task> Hook)> Hooks = new();
    private static readonly Lock Gate = new();

    public static void Register(string name, Func<Task> hook)
    {
        lock (Gate) Hooks.Add((name, hook));
    }

    public static async Task RunAll(ILogger logger)
    {
        List<(string Name, Func<Task> Hook)> snapshot;
        lock (Gate) snapshot = Hooks.ToList();
        foreach (var (name, hook) in snapshot.AsEnumerable().Reverse())
        {
            try { await hook(); }
            catch (Exception error) { logger.LogWarning(error, "Shutdown hook {Hook} failed", name); }
        }
    }
}

/// <summary>
/// SIGTERM stops new connections; requests get 20 s. A watchdog kills the process at 25 s
/// with Process.Kill, not Environment.Exit, which would wait on the same stuck shutdown.
/// </summary>
public sealed class ShutdownWatchdog(ILogger<ShutdownWatchdog> logger) : IHostedService
{
    private CancellationTokenRegistration? _registration;

    public Task StartAsync(CancellationToken cancellation)
    {
        _registration = cancellation.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(25));
                logger.LogWarning("Shutdown exceeded 25s; killing the process");
                try { Environment.ExitCode = 1; } catch { }
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            });
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellation)
    {
        _registration?.Dispose();
        return Task.CompletedTask;
    }
}
