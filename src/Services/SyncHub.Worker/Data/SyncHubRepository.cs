using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using SyncHub.Core.Matching;
using SyncHub.Core.Models;

namespace SyncHub.Worker.Data;

public sealed class SqlConnectionFactory(IConfiguration configuration)
{
    /// <summary>Creates an unopened connection from the named connection string (<c>SyncHub</c> or <c>FirmA</c>).</summary>
    /// <exception cref="InvalidOperationException">The connection string isn't configured.</exception>
    public SqlConnection Create(string name) =>
        new(configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' is missing from configuration."));
}

public sealed class MergeResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Failed { get; set; }
}

public sealed record OpenDeadLetterStat(string Source, string Entity, string ErrorType, int OpenCount, int OldestAgeSeconds);
public sealed record StuckRunStat(string Source, string RunType, int StuckRuns);
/// <summary>A class, not a positional record: usp_GetReconciliation returns more columns than we need,
/// and Dapper only maps to a record constructor when the column list matches it exactly.</summary>
public sealed class ReconciliationStat
{
    public string Source { get; init; } = "";
    public string Entity { get; init; } = "";
    public int Unexplained { get; init; }
}

/// <summary>State that only lives in SQL, read periodically and published as gauges.</summary>
public sealed record HealthSnapshot(
    IReadOnlyList<OpenDeadLetterStat> DeadLetters,
    IReadOnlyList<StuckRunStat> StuckRuns,
    IReadOnlyList<ReconciliationStat> Reconciliation,
    DateTimeOffset TakenAt);

public sealed record ReplayItem(long DeadLetterId, string Source, string Entity, string SourceKey, string Payload);

public sealed record EntitySyncResult(
    string Entity, int? SourceTotal, int Read, int Staged, int Inserted, int Updated, int Failed);

/// <summary>
/// All SyncHub database access for the worker. Stored procedures do the set-based
/// work; this class keeps the C# side thin and parameterised.
/// </summary>
public sealed class SyncHubRepository(SqlConnectionFactory connections)
{
    private const string Db = "SyncHub";
    private const int LongCommandTimeoutSeconds = 300;

    /// <summary>Records the start of a sync or replay run in ops.SyncRun.</summary>
    /// <param name="runType"><c>Scheduled</c> or <c>Replay</c>.</param>
    /// <param name="correlationId">Also written on every log line for this run, linking the dashboard to the logs.</param>
    /// <param name="source">The source system being synced or replayed.</param>
    /// <returns>The new SyncRunId, used to tag staged rows and dead letters.</returns>
    public async Task<long> StartRunAsync(string source, string runType, Guid correlationId)
    {
        await using var conn = connections.Create(Db);
        return await conn.QuerySingleAsync<long>("ops.usp_StartSyncRun",
            new { Source = source, RunType = runType, CorrelationId = correlationId },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>Marks a run finished with its final status (<c>Succeeded</c>, <c>SucceededWithErrors</c> or <c>Failed</c>).</summary>
    /// <param name="errorMessage">Why the run failed, shown on the dashboard; <c>null</c> on success.</param>
    /// <param name="syncRunId">The run.</param>
    /// <param name="status"><c>Succeeded</c>, <c>SucceededWithErrors</c> or <c>Failed</c>.</param>
    public async Task CompleteRunAsync(long syncRunId, string status, string? errorMessage)
    {
        await using var conn = connections.Create(Db);
        await conn.ExecuteAsync("ops.usp_CompleteSyncRun",
            new { SyncRunId = syncRunId, Status = status, ErrorMessage = errorMessage },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>Saves the per-entity counts for a run (read, staged, inserted, updated, failed, and the source total used by reconciliation).</summary>
    public async Task SaveEntityResultAsync(long syncRunId, EntitySyncResult r)
    {
        await using var conn = connections.Create(Db);
        await conn.ExecuteAsync("ops.usp_SaveSyncRunEntity",
            new
            {
                SyncRunId = syncRunId, r.Entity, r.SourceTotal,
                RecordsRead = r.Read, RecordsStaged = r.Staged,
                RecordsInserted = r.Inserted, RecordsUpdated = r.Updated, RecordsFailed = r.Failed
            },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>The highest source timestamp already merged for a source and entity, in the source's own time basis.</summary>
    /// <returns><c>null</c> before the first successful sync, which means a full load.</returns>
    public async Task<DateTime?> GetWatermarkAsync(string source, string entity)
    {
        await using var conn = connections.Create(Db);
        return await conn.QuerySingleOrDefaultAsync<DateTime?>("ops.usp_GetWatermark",
            new { Source = source, Entity = entity }, commandType: CommandType.StoredProcedure);
    }

    /// <summary>Moves the watermark. Only call this after the merge has committed, so a failure re-reads the same records.</summary>
    public async Task SetWatermarkAsync(string source, string entity, DateTime lastModified)
    {
        await using var conn = connections.Create(Db);
        await conn.ExecuteAsync("ops.usp_SetWatermark",
            new { Source = source, Entity = entity, LastModified = lastModified },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>
    /// Records a record that couldn't be mapped or merged, with its raw payload so support can fix and replay it.
    /// If the record already has an open dead letter, that one is updated rather than a duplicate added.
    /// </summary>
    public async Task UpsertDeadLetterAsync(long syncRunId, string source, string entity, string sourceKey,
        string errorType, string errorMessage, string payload)
    {
        await using var conn = connections.Create(Db);
        await conn.ExecuteAsync("ops.usp_UpsertDeadLetter",
            new
            {
                SyncRunId = syncRunId, Source = source, Entity = entity, SourceKey = sourceKey,
                ErrorType = errorType, ErrorMessage = Truncate(errorMessage, 2000), Payload = payload
            },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>Dead letters that support has queued for replay (status <c>ReplayRequested</c>), with their payloads.</summary>
    public async Task<IReadOnlyList<ReplayItem>> GetReplayQueueAsync()
    {
        await using var conn = connections.Create(Db);
        var rows = await conn.QueryAsync<ReplayItem>("ops.usp_GetReplayQueue", commandType: CommandType.StoredProcedure);
        return rows.AsList();
    }

    /* ---------------- Staging (SqlBulkCopy) ---------------- */

    /// <summary>Writes mapped clients to stg.Client with SqlBulkCopy, tagged with the run id the merge will read.</summary>
    /// <returns>The number of rows staged.</returns>
    public async Task<int> BulkStageClientsAsync(long syncRunId, IReadOnlyCollection<StagedClient> clients, CancellationToken ct)
    {
        if (clients.Count == 0) return 0;

        var table = new DataTable();
        table.Columns.Add("SyncRunId", typeof(long));
        table.Columns.Add("Source", typeof(string));
        table.Columns.Add("SourceClientKey", typeof(string));
        table.Columns.Add("ClientName", typeof(string));
        table.Columns.Add("NormalisedName", typeof(string));
        table.Columns.Add("Country", typeof(string));
        table.Columns.Add("PostCode", typeof(string));
        table.Columns.Add("OpenedOn", typeof(DateTime));
        table.Columns.Add("IsActive", typeof(bool));
        table.Columns.Add("SourceModifiedUtc", typeof(DateTime));
        table.Columns.Add("RawPayload", typeof(string));

        foreach (var c in clients)
        {
            table.Rows.Add(syncRunId, c.Source, c.SourceClientKey, c.ClientName, c.NormalisedName,
                (object?)c.Country ?? DBNull.Value, (object?)c.PostCode ?? DBNull.Value,
                (object?)c.OpenedOn ?? DBNull.Value, c.IsActive, c.SourceModifiedUtc, c.RawPayload);
        }

        await BulkCopyAsync("stg.Client", table, ct);
        return clients.Count;
    }

    /// <summary>Writes mapped matters to stg.Matter with SqlBulkCopy, tagged with the run id the merge will read.</summary>
    /// <returns>The number of rows staged.</returns>
    public async Task<int> BulkStageMattersAsync(long syncRunId, IReadOnlyCollection<StagedMatter> matters, CancellationToken ct)
    {
        if (matters.Count == 0) return 0;

        var table = new DataTable();
        table.Columns.Add("SyncRunId", typeof(long));
        table.Columns.Add("Source", typeof(string));
        table.Columns.Add("SourceMatterKey", typeof(string));
        table.Columns.Add("SourceClientKey", typeof(string));
        table.Columns.Add("MatterNumber", typeof(string));
        table.Columns.Add("Description", typeof(string));
        table.Columns.Add("PracticeAreaCode", typeof(string));
        table.Columns.Add("FeeEarner", typeof(string));
        table.Columns.Add("OpenedOn", typeof(DateTime));
        table.Columns.Add("ClosedOn", typeof(DateTime));
        table.Columns.Add("IsOpen", typeof(bool));
        table.Columns.Add("SourceModifiedUtc", typeof(DateTime));
        table.Columns.Add("RawPayload", typeof(string));

        foreach (var m in matters)
        {
            table.Rows.Add(syncRunId, m.Source, m.SourceMatterKey, m.SourceClientKey, m.MatterNumber,
                (object?)m.Description ?? DBNull.Value, (object?)m.PracticeAreaCode ?? DBNull.Value,
                (object?)m.FeeEarner ?? DBNull.Value, (object?)m.OpenedOn ?? DBNull.Value,
                (object?)m.ClosedOn ?? DBNull.Value, m.IsOpen, m.SourceModifiedUtc, m.RawPayload);
        }

        await BulkCopyAsync("stg.Matter", table, ct);
        return matters.Count;
    }

    /// <summary>Bulk-inserts a table into staging in one batch, mapping columns by name so identity and default columns are left to SQL Server.</summary>
    private async Task BulkCopyAsync(string destination, DataTable table, CancellationToken ct)
    {
        await using var conn = connections.Create(Db);
        await conn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = destination,
            BatchSize = table.Rows.Count,
            BulkCopyTimeout = LongCommandTimeoutSeconds
        };
        // Map by name so the identity and default columns in staging are left alone
        foreach (DataColumn column in table.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);

        await bulk.WriteToServerAsync(table, ct);
    }

    /* ---------------- Merge ---------------- */

    /// <summary>
    /// Runs the set-based MERGE from staging into core for one entity and run. Idempotent: unchanged rows
    /// (same row hash) aren't written, so re-running a batch is safe.
    /// </summary>
    /// <param name="entity"><see cref="Entities.Client"/> or <see cref="Entities.Matter"/>.</param>
    /// <param name="isReplay">Apply newer-wins: a replayed payload never overwrites a newer version. Scheduled reads skip it.</param>
    /// <param name="syncRunId">The run whose staged rows to merge.</param>
    /// <returns>Rows inserted, updated, and failed (orphan matters dead-lettered during the merge).</returns>
    public async Task<MergeResult> MergeAsync(string entity, long syncRunId, bool isReplay = false)
    {
        var proc = entity switch
        {
            Entities.Client => "core.usp_MergeClients",
            Entities.Matter => "core.usp_MergeMatters",
            _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null)
        };

        await using var conn = connections.Create(Db);
        return await conn.QuerySingleAsync<MergeResult>(proc, new { SyncRunId = syncRunId, IsReplay = isReplay },
            commandType: CommandType.StoredProcedure, commandTimeout: LongCommandTimeoutSeconds);
    }

    /* ---------------- Health snapshot (monitoring) ---------------- */

    /// <summary>Reads the state behind the SQL-backed gauges: open dead letters, stuck runs and reconciliation.</summary>
    public async Task<HealthSnapshot> GetHealthSnapshotAsync(DateTimeOffset takenAt, CancellationToken ct)
    {
        await using var conn = connections.Create(Db);
        IReadOnlyList<OpenDeadLetterStat> deadLetters;
        IReadOnlyList<StuckRunStat> stuck;
        await using (var grid = await conn.QueryMultipleAsync(new CommandDefinition(
                         "ops.usp_GetHealthSnapshot", commandType: CommandType.StoredProcedure, cancellationToken: ct)))
        {
            deadLetters = (await grid.ReadAsync<OpenDeadLetterStat>()).AsList();
            stuck = (await grid.ReadAsync<StuckRunStat>()).AsList();
        }

        // Reuse the reconciliation the dashboard shows, so the alert and the screen always agree.
        var reconciliation = (await conn.QueryAsync<ReconciliationStat>(new CommandDefinition(
            "ops.usp_GetReconciliation", commandType: CommandType.StoredProcedure,
            commandTimeout: LongCommandTimeoutSeconds, cancellationToken: ct))).AsList();

        return new HealthSnapshot(deadLetters, stuck, reconciliation, takenAt);
    }

    /* ---------------- Duplicates, reporting, housekeeping ---------------- */

    /// <summary>Every client in the hub with the fields duplicate detection needs (normalised name, postcode, source).</summary>
    public async Task<IReadOnlyList<ClientForMatching>> GetClientsForMatchingAsync()
    {
        await using var conn = connections.Create(Db);
        var rows = await conn.QueryAsync<ClientForMatching>("core.usp_GetClientsForMatching",
            commandType: CommandType.StoredProcedure);
        return rows.AsList();
    }

    /// <summary>
    /// Saves suggested duplicate pairs in one round trip (a table-valued parameter). Pairs a person has
    /// already decided are left alone.
    /// </summary>
    /// <returns>How many new pairs were added for review.</returns>
    public async Task<int> SaveMatchCandidatesAsync(IReadOnlyCollection<MatchCandidate> candidates)
    {
        if (candidates.Count == 0) return 0;

        // A table-valued parameter sends the whole set in one round trip
        var table = new DataTable();
        table.Columns.Add("ClientIdA", typeof(int));
        table.Columns.Add("ClientIdB", typeof(int));
        table.Columns.Add("Score", typeof(decimal));
        table.Columns.Add("MatchReason", typeof(string));
        foreach (var c in candidates)
            table.Rows.Add(c.ClientIdA, c.ClientIdB, c.Score, Truncate(c.Reason, 200));

        await using var conn = connections.Create(Db);
        return await conn.QuerySingleAsync<int>("core.usp_SaveMatchCandidates",
            new { Candidates = table.AsTableValuedParameter("core.MatchCandidateList") },
            commandType: CommandType.StoredProcedure);
    }

    /// <summary>Rebuilds the reporting star schema (rpt.*) that SSRS and Power BI read.</summary>
    public async Task LoadReportingAsync()
    {
        await using var conn = connections.Create(Db);
        await conn.ExecuteAsync("rpt.usp_LoadReporting", commandType: CommandType.StoredProcedure,
            commandTimeout: LongCommandTimeoutSeconds);
    }

    /// <summary>Deletes staging rows from runs older than <paramref name="keepDays"/> days, in chunks.</summary>
    public async Task PurgeStagingAsync(int keepDays)
    {
        await using var conn = connections.Create(Db);
        await conn.ExecuteAsync("ops.usp_PurgeStaging", new { KeepDays = keepDays },
            commandType: CommandType.StoredProcedure, commandTimeout: LongCommandTimeoutSeconds);
    }

    /// <summary>
    /// Deletes old sync runs and their per-entity rows, in chunks: successful runs after
    /// <paramref name="keepSucceededDays"/>, failed ones after <paramref name="keepOtherDays"/>. Running runs are kept.
    /// </summary>
    /// <returns>How many runs were purged.</returns>
    public async Task<int> PurgeRunHistoryAsync(int keepSucceededDays, int keepOtherDays)
    {
        await using var conn = connections.Create(Db);
        return await conn.QuerySingleAsync<int>("ops.usp_PurgeRunHistory",
            new { KeepSucceededDays = keepSucceededDays, KeepOtherDays = keepOtherDays },
            commandType: CommandType.StoredProcedure, commandTimeout: LongCommandTimeoutSeconds);
    }

    /// <summary>Opens a connection to SyncHub; throws if the database is unreachable or the login fails.</summary>
    public async Task CheckConnectivityAsync(CancellationToken ct)
    {
        await using var conn = connections.Create(Db);
        await conn.OpenAsync(ct);
    }

    /// <summary>Shortens text to a column's maximum length so a long error message can't fail the insert.</summary>
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
