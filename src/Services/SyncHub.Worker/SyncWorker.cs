using System.Diagnostics;
using Microsoft.Extensions.Options;
using SyncHub.Core.Models;
using SyncHub.Worker.Data;
using SyncHub.Worker.Pipeline;
using SyncHub.Worker.Sources;

namespace SyncHub.Worker;

/// <summary>
/// One full cycle. Each step is isolated: a failure in one (Firm B being down, say)
/// is logged and the remaining steps still run.
/// Scoped, because the Firm B connector wraps a typed HttpClient that should not be
/// held for the lifetime of the app.
/// </summary>
public sealed class SyncCycle(
    IEnumerable<ISourceConnector> connectors,
    SourceSyncService sourceSync,
    ReplayService replay,
    DuplicateDetectionService duplicates,
    HousekeepingService housekeeping,
    HealthSnapshotService health,
    SyncMetrics metrics,
    ILogger<SyncCycle> logger)
{
    /// <summary>Runs every step of one cycle in order: replay, sync each source, duplicate detection, reporting refresh, purge of old data (staging and run history), health snapshot.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await StepAsync("Replay queued dead letters", () => replay.RunAsync(ct), ct);

        foreach (var connector in connectors)
            await StepAsync($"Sync {connector.Source}", () => sourceSync.SyncAsync(connector, ct), ct);

        await StepAsync("Duplicate detection", duplicates.RunAsync, ct);
        await StepAsync("Reporting refresh", housekeeping.RefreshReportingIfDueAsync, ct);
        await StepAsync("Purge old data", housekeeping.PurgeOldDataIfDueAsync, ct);
        await StepAsync("Health snapshot", () => health.RefreshIfDueAsync(ct), ct);
    }

    /// <summary>Runs one step; logs and counts a failure instead of throwing, so the remaining steps still run.</summary>
    private async Task StepAsync(string name, Func<Task> step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.StepFailed(name);
            logger.LogError(ex, "Step '{Step}' failed; continuing with the rest of the cycle", name);
        }
    }
}

public sealed class SyncWorker(
    IServiceScopeFactory scopeFactory,
    SyncHubRepository repository,
    SyncMetrics metrics,
    IOptions<SyncOptions> options,
    ILogger<SyncWorker> logger) : BackgroundService
{
    /// <summary>
    /// Checks the database once, then runs a cycle immediately and every <c>Sync:IntervalSeconds</c> until shutdown.
    /// Each cycle gets its own DI scope. Starts the time-since-success gauges first, so a worker that can't
    /// reach SQL still trips the SyncStale alert.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before the database check, so time_since_success grows from start-up even if the worker
        // gives up below: SyncStale then fires instead of the series silently never existing.
        foreach (var source in (string[])[SourceSystems.FirmA, SourceSystems.FirmB])
            metrics.TrackSource(source);

        if (!await CanReachDatabaseAsync(stoppingToken)) return;

        var interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
        logger.LogInformation("SyncHub worker started; running every {Interval}", interval);

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                var stopwatch = Stopwatch.StartNew();
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<SyncCycle>().RunAsync(stoppingToken);
                }
                metrics.CycleCompleted(stopwatch.Elapsed);
                logger.LogInformation("Cycle complete in {Elapsed:N1}s", stopwatch.Elapsed.TotalSeconds);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("SyncHub worker stopping");
        }
    }

    /// <summary>Checks the SyncHub database is reachable before the first cycle.</summary>
    /// <returns><c>false</c> (after logging a critical error with the likely fix) if it can't be reached.</returns>
    private async Task<bool> CanReachDatabaseAsync(CancellationToken ct)
    {
        try
        {
            await repository.CheckConnectivityAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex,
                "Cannot connect to the SyncHub database. Check ConnectionStrings:SyncHub in appsettings.json " +
                "and that the database scripts in /database have been run.");
            return false;
        }
    }
}
