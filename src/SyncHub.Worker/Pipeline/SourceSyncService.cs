using Microsoft.Extensions.Options;
using SyncHub.Core.Mapping;
using SyncHub.Core.Models;
using SyncHub.Worker.Data;
using SyncHub.Worker.Sources;

namespace SyncHub.Worker.Pipeline;

/// <summary>Finds the mapper for a source system.</summary>
public sealed class MapperRegistry(IEnumerable<ISourceMapper> mappers)
{
    private readonly Dictionary<string, ISourceMapper> _bySource = mappers.ToDictionary(m => m.Source);

    /// <summary>The mapper registered for <paramref name="source"/>.</summary>
    /// <exception cref="InvalidOperationException">No mapper is registered for that source.</exception>
    public ISourceMapper For(string source) =>
        _bySource.TryGetValue(source, out var mapper)
            ? mapper
            : throw new InvalidOperationException($"No mapper registered for source '{source}'.");
}

/// <summary>
/// One scheduled sync of one source system:
///   read changes since the watermark -> map and validate -> bulk stage -> set-based merge -> move the watermark.
///
/// The watermark only moves after the merge has committed. If anything fails part-way,
/// the next run re-reads the same records, and because the merge is idempotent that is safe.
/// </summary>
public sealed class SourceSyncService(
    SyncHubRepository repository,
    MapperRegistry mappers,
    IOptions<SyncOptions> options,
    SyncMetrics metrics,
    TimeProvider clock,
    ILogger<SourceSyncService> logger)
{
    private static readonly string[] EntityOrder = [Entities.Client, Entities.Matter];   // clients first: matters need them
    private const int FirmALocalTimeLookbackMinutes = 65;

    /// <summary>
    /// One scheduled run for one source: clients first (matters need them), then matters. Records the run
    /// in ops.SyncRun and its outcome in metrics. A failure fails this run only; the next cycle retries from
    /// the same watermark. Cancellation (shutdown) marks the run failed and rethrows.
    /// </summary>
    public async Task SyncAsync(ISourceConnector connector, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid();
        using var _ = logger.BeginScope(new Dictionary<string, object>
        {
            ["Source"] = connector.Source,
            ["CorrelationId"] = correlationId
        });

        var started = clock.GetTimestamp();
        var outcome = "Failed";
        var runId = await repository.StartRunAsync(connector.Source, "Scheduled", correlationId);
        logger.LogInformation("Sync run {SyncRunId} started", runId);

        try
        {
            var results = new List<EntitySyncResult>();
            foreach (var entity in EntityOrder)
            {
                var result = await SyncEntityAsync(runId, connector, entity, ct);
                await repository.SaveEntityResultAsync(runId, result);
                results.Add(result);
            }

            var status = results.Any(r => r.Failed > 0) ? "SucceededWithErrors" : "Succeeded";
            await repository.CompleteRunAsync(runId, status, null);
            outcome = status;
            logger.LogInformation("Sync run {SyncRunId} finished: {Status}", runId, status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await repository.CompleteRunAsync(runId, "Failed", "Cancelled because the service is stopping.");
            throw;
        }
        catch (Exception ex)
        {
            // Fail this run but not the whole worker: the other source can still sync,
            // and this one will be retried from the same watermark next cycle.
            logger.LogError(ex, "Sync run {SyncRunId} failed", runId);
            await repository.CompleteRunAsync(runId, "Failed", Truncate(ex.Message, 4000));
        }
        finally
        {
            metrics.RunCompleted(connector.Source, outcome, clock.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// Reads changes since the watermark (minus the look-back), maps each record, stages in batches,
    /// merges, then moves the watermark to the newest timestamp read. Firm A looks back at least 65 minutes
    /// because its UK local timestamps repeat an hour when the clocks go back.
    /// </summary>
    private async Task<EntitySyncResult> SyncEntityAsync(long runId, ISourceConnector connector, string entity, CancellationToken ct)
    {
        var source = connector.Source;
        var mapper = mappers.For(source);
        var settings = options.Value;

        var watermark = await repository.GetWatermarkAsync(source, entity);
        // Firm A's timestamps are UK local time, so when the clocks go back 01:00-02:00 happens twice.
        // An edit stamped 01:10 GMT can land after the watermark reached 01:55 BST; looking back at
        // least an hour still picks it up. Re-reading is safe because the merge is idempotent.
        var lookbackMinutes = source == SourceSystems.FirmA
            ? Math.Max(settings.LookbackMinutes, FirmALocalTimeLookbackMinutes)
            : settings.LookbackMinutes;
        DateTime? since = watermark?.AddMinutes(-lookbackMinutes);
        var sourceTotal = await connector.GetSourceTotalAsync(entity, ct);

        logger.LogInformation("{Entity}: reading changes since {Since} (source holds {SourceTotal})",
            entity, since?.ToString("u") ?? "the beginning", sourceTotal);

        int read = 0, staged = 0, failed = 0;
        DateTime? maxModified = null;
        var clients = new List<StagedClient>();
        var matters = new List<StagedMatter>();

        await foreach (var record in connector.ReadChangesAsync(entity, since, ct))
        {
            read++;
            if (record.SourceModified > DateTime.MinValue && (maxModified is null || record.SourceModified > maxModified))
                maxModified = record.SourceModified;

            if (entity == Entities.Client)
            {
                var result = mapper.MapClient(record.SourceKey, record.RawJson);
                if (result.IsSuccess) clients.Add(result.Value!);
                else { failed++; await DeadLetterAsync(runId, record, result.ErrorType!, result.ErrorMessage!); }
            }
            else
            {
                var result = mapper.MapMatter(record.SourceKey, record.RawJson);
                if (result.IsSuccess) matters.Add(result.Value!);
                else { failed++; await DeadLetterAsync(runId, record, result.ErrorType!, result.ErrorMessage!); }
            }

            if (clients.Count + matters.Count >= settings.BatchSize)
                staged += await FlushAsync(runId, clients, matters, ct);
        }
        staged += await FlushAsync(runId, clients, matters, ct);

        var merge = await repository.MergeAsync(entity, runId);
        failed += merge.Failed;   // e.g. orphan matters found during the merge

        metrics.RecordsRead(source, entity, read);
        metrics.RecordsMerged(source, entity, merge.Inserted, merge.Updated);
        if (merge.Failed > 0)
            metrics.RecordsRejected(source, entity, "OrphanMatter", merge.Failed);

        if (maxModified is not null)
            await repository.SetWatermarkAsync(source, entity, maxModified.Value);

        logger.LogInformation(
            "{Entity}: read {Read}, staged {Staged}, inserted {Inserted}, updated {Updated}, failed {Failed}",
            entity, read, staged, merge.Inserted, merge.Updated, failed);

        return new EntitySyncResult(entity, sourceTotal, read, staged, merge.Inserted, merge.Updated, failed);
    }

    /// <summary>Bulk-stages the buffered clients and matters and clears the buffers. <returns>Rows staged.</returns></summary>
    private async Task<int> FlushAsync(long runId, List<StagedClient> clients, List<StagedMatter> matters, CancellationToken ct)
    {
        var count = await repository.BulkStageClientsAsync(runId, clients, ct)
                    + await repository.BulkStageMattersAsync(runId, matters, ct);
        clients.Clear();
        matters.Clear();
        return count;
    }

    /// <summary>Logs, counts and dead-letters a record that failed mapping, keeping its raw payload for replay.</summary>
    private Task DeadLetterAsync(long runId, SourceRecord record, string errorType, string message)
    {
        metrics.RecordsRejected(record.Source, record.Entity, errorType);
        logger.LogWarning("{Entity} {SourceKey} rejected: {ErrorType} - {Message}",
            record.Entity, record.SourceKey, errorType, message);
        return repository.UpsertDeadLetterAsync(runId, record.Source, record.Entity, record.SourceKey,
            errorType, message, record.RawJson);
    }

    /// <summary>Shortens text to fit ops.SyncRun.ErrorMessage.</summary>
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
