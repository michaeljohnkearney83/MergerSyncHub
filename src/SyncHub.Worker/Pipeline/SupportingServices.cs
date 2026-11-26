using Microsoft.Extensions.Options;
using SyncHub.Core.Matching;
using SyncHub.Core.Models;
using SyncHub.Worker.Data;

namespace SyncHub.Worker.Pipeline;

/// <summary>
/// Replays dead letters that a support analyst queued from the dashboard (or that were
/// queued automatically because the client they were waiting for has arrived).
/// The stored payload goes through the same mapper and merge as live data.
/// </summary>
public sealed class ReplayService(SyncHubRepository repository, MapperRegistry mappers, ILogger<ReplayService> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var queue = await repository.GetReplayQueueAsync();
        if (queue.Count == 0) return;

        foreach (var group in queue.GroupBy(i => i.Source))
        {
            var correlationId = Guid.NewGuid();
            using var _ = logger.BeginScope(new Dictionary<string, object>
            {
                ["Source"] = group.Key,
                ["CorrelationId"] = correlationId
            });

            var runId = await repository.StartRunAsync(group.Key, "Replay", correlationId);
            logger.LogInformation("Replay run {SyncRunId}: {Count} item(s)", runId, group.Count());

            try
            {
                var mapper = mappers.For(group.Key);
                var clients = new List<StagedClient>();
                var matters = new List<StagedMatter>();
                int clientFailed = 0, matterFailed = 0;

                foreach (var item in group)
                {
                    if (item.Entity == Entities.Client)
                    {
                        var r = mapper.MapClient(item.SourceKey, item.Payload);
                        if (r.IsSuccess) clients.Add(r.Value!);
                        else { clientFailed++; await FailAgainAsync(runId, item, r.ErrorType!, r.ErrorMessage!); }
                    }
                    else
                    {
                        var r = mapper.MapMatter(item.SourceKey, item.Payload);
                        if (r.IsSuccess) matters.Add(r.Value!);
                        else { matterFailed++; await FailAgainAsync(runId, item, r.ErrorType!, r.ErrorMessage!); }
                    }
                }

                // Stage and merge clients before matters so an orphan can find its client in the same replay
                var stagedClients = await repository.BulkStageClientsAsync(runId, clients, ct);
                var clientMerge = await repository.MergeAsync(Entities.Client, runId);
                var stagedMatters = await repository.BulkStageMattersAsync(runId, matters, ct);
                var matterMerge = await repository.MergeAsync(Entities.Matter, runId);

                var clientCount = group.Count(i => i.Entity == Entities.Client);
                var matterCount = group.Count() - clientCount;
                await repository.SaveEntityResultAsync(runId, new EntitySyncResult(Entities.Client, null, clientCount,
                    stagedClients, clientMerge.Inserted, clientMerge.Updated, clientFailed + clientMerge.Failed));
                await repository.SaveEntityResultAsync(runId, new EntitySyncResult(Entities.Matter, null, matterCount,
                    stagedMatters, matterMerge.Inserted, matterMerge.Updated, matterFailed + matterMerge.Failed));

                var failed = clientFailed + matterFailed + matterMerge.Failed;
                var status = failed > 0 ? "SucceededWithErrors" : "Succeeded";
                await repository.CompleteRunAsync(runId, status, null);
                logger.LogInformation("Replay run {SyncRunId} finished: {Status} ({Failed} still failing)", runId, status, failed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Replay run {SyncRunId} failed", runId);
                await repository.CompleteRunAsync(runId, "Failed", ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message);
            }
        }
    }

    private Task FailAgainAsync(long runId, ReplayItem item, string errorType, string message)
    {
        logger.LogWarning("Replay of {Entity} {SourceKey} failed again: {ErrorType} - {Message}",
            item.Entity, item.SourceKey, errorType, message);
        return repository.UpsertDeadLetterAsync(runId, item.Source, item.Entity, item.SourceKey, errorType, message, item.Payload);
    }
}

public sealed class DuplicateDetectionService(
    SyncHubRepository repository, IOptions<SyncOptions> options, ILogger<DuplicateDetectionService> logger)
{
    public async Task RunAsync()
    {
        var clients = await repository.GetClientsForMatchingAsync();
        var candidates = new DuplicateDetector(options.Value.DuplicateThreshold).FindCandidates(clients);
        var newCount = await repository.SaveMatchCandidatesAsync(candidates);
        if (newCount > 0)
            logger.LogInformation("Duplicate detection: {New} new possible duplicate(s) for review ({Total} scored)",
                newCount, candidates.Count);
    }
}

/// <summary>Refreshes the reporting star schema and purges old staging rows, each on its own interval.</summary>
public sealed class HousekeepingService(
    SyncHubRepository repository, IOptions<SyncOptions> options, TimeProvider clock, ILogger<HousekeepingService> logger)
{
    private DateTimeOffset _lastReportingRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public async Task RefreshReportingIfDueAsync()
    {
        var now = clock.GetUtcNow();
        if (now - _lastReportingRefresh < TimeSpan.FromMinutes(options.Value.ReportingRefreshMinutes)) return;

        await repository.LoadReportingAsync();
        _lastReportingRefresh = now;
        logger.LogInformation("Reporting star schema refreshed");
    }

    public async Task PurgeStagingIfDueAsync()
    {
        var now = clock.GetUtcNow();
        if (now - _lastPurge < TimeSpan.FromHours(24)) return;

        await repository.PurgeStagingAsync(options.Value.StagingRetentionDays);
        _lastPurge = now;
        logger.LogInformation("Purged staging rows older than {Days} days", options.Value.StagingRetentionDays);
    }
}
