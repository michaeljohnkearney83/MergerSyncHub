using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SyncHub.Core.Models;
using SyncHub.Worker.Data;

namespace SyncHub.IntegrationTests;

/// <summary>
/// The guarantees the sync engine relies on, tested against the real stored procedures through the
/// worker's own repository (the same SqlBulkCopy staging and MERGE calls a live sync makes):
///   * re-running a batch changes nothing (so retries and the look-back window are safe)
///   * a stale replay never overwrites newer data, but a scheduled read always wins
///   * reconciliation counts each missing record once
/// </summary>
[Collection("sql")]
public sealed class MergeTests(SqlServerFixture sql)
{
    /// <summary>The worker's real repository, pointed at the test database.</summary>
    private SyncHubRepository Repository()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SyncHub"] = sql.SyncHubConnectionString })
            .Build();
        return new SyncHubRepository(new SqlConnectionFactory(config));
    }

    /// <summary>A valid mapped client, as a mapper would produce it.</summary>
    private static StagedClient Client(string source, string key, string name, DateTime modifiedUtc) =>
        new(source, key, name, name.ToUpperInvariant(), "United Kingdom", "L1 1AA",
            new DateTime(2020, 1, 1), IsActive: true, modifiedUtc, RawPayload: "{}");

    /// <summary>Stage one client in a new run and merge it, the way SourceSyncService does.</summary>
    private async Task<MergeResult> SyncAsync(StagedClient client, string runType = "Scheduled")
    {
        var repo = Repository();
        var runId = await repo.StartRunAsync(client.Source, runType, Guid.NewGuid());
        await repo.BulkStageClientsAsync(runId, [client], CancellationToken.None);
        var result = await repo.MergeAsync(Entities.Client, runId, isReplay: runType == "Replay");
        await repo.CompleteRunAsync(runId, "Succeeded", null);
        return result;
    }

    /// <summary>The client's name as it now stands in core.Client.</summary>
    private async Task<string> HubNameAsync(string source, string key)
    {
        await using var conn = new SqlConnection(sql.SyncHubConnectionString);
        return await conn.QuerySingleAsync<string>(
            "SELECT ClientName FROM core.Client WHERE Source = @source AND SourceClientKey = @key", new { source, key });
    }

    [Fact]
    public async Task Merging_the_same_record_twice_changes_nothing_the_second_time()
    {
        var source = SqlServerFixture.NewSource();
        var record = Client(source, "C1", "Acme Holdings Ltd", new DateTime(2026, 3, 1, 9, 0, 0));

        var first = await SyncAsync(record);
        var second = await SyncAsync(record);

        Assert.Equal((1, 0), (first.Inserted, first.Updated));
        Assert.Equal((0, 0), (second.Inserted, second.Updated));   // row hash unchanged: no write
    }

    [Fact]
    public async Task A_changed_record_is_updated()
    {
        var source = SqlServerFixture.NewSource();
        await SyncAsync(Client(source, "C1", "Acme Holdings Ltd", new DateTime(2026, 3, 1, 9, 0, 0)));

        var result = await SyncAsync(Client(source, "C1", "Acme Holdings Limited", new DateTime(2026, 3, 1, 10, 0, 0)));

        Assert.Equal(1, result.Updated);
        Assert.Equal("Acme Holdings Limited", await HubNameAsync(source, "C1"));
    }

    [Fact]
    public async Task A_stale_replay_does_not_overwrite_newer_data()
    {
        var source = SqlServerFixture.NewSource();
        await SyncAsync(Client(source, "C1", "Current Name Ltd", new DateTime(2026, 3, 2)));

        // A dead letter from the day before is fixed and replayed after the record has moved on.
        var result = await SyncAsync(Client(source, "C1", "Old Name Ltd", new DateTime(2026, 3, 1)), runType: "Replay");

        Assert.Equal(0, result.Updated);
        Assert.Equal("Current Name Ltd", await HubNameAsync(source, "C1"));
    }

    [Fact]
    public async Task A_scheduled_read_wins_even_when_its_local_time_stamp_looks_older()
    {
        // When UK clocks go back, 01:00-02:00 happens twice. An edit at 01:50 BST is stored as 01:50,
        // and a genuinely later edit at 01:10 GMT is stamped 01:10. A scheduled read is the source's
        // current state, so it must be applied even though its timestamp is "earlier".
        var source = SqlServerFixture.NewSource();
        await SyncAsync(Client(source, "C1", "Before Clock Change Ltd", new DateTime(2026, 10, 25, 1, 50, 0)));

        var result = await SyncAsync(Client(source, "C1", "After Clock Change Ltd", new DateTime(2026, 10, 25, 1, 10, 0)));

        Assert.Equal(1, result.Updated);
        Assert.Equal("After Clock Change Ltd", await HubNameAsync(source, "C1"));
    }

    [Fact]
    public async Task Reconciliation_counts_a_record_that_was_ignored_and_failed_again_once()
    {
        var source = SqlServerFixture.NewSource();
        var repo = Repository();

        // The source holds 2 clients: one merges, one keeps failing validation.
        await SyncAsync(Client(source, "GOOD", "Good Client Ltd", new DateTime(2026, 3, 1)));
        var runId = await repo.StartRunAsync(source, "Scheduled", Guid.NewGuid());
        await repo.SaveEntityResultAsync(runId, new EntitySyncResult(Entities.Client, SourceTotal: 2, 2, 1, 0, 0, 1));
        await repo.CompleteRunAsync(runId, "SucceededWithErrors", null);

        // The bad record was ignored by an analyst, then failed again: two dead-letter rows, one record.
        await using (var conn = new SqlConnection(sql.SyncHubConnectionString))
        {
            await conn.ExecuteAsync("""
                INSERT ops.DeadLetter (FirstSyncRunId, LastSyncRunId, Source, Entity, SourceKey, ErrorType, ErrorMessage, Payload, Status)
                VALUES (@runId, @runId, @source, 'Client', 'BAD', 'MissingClientName', N'test', N'{}', 'Ignored'),
                       (@runId, @runId, @source, 'Client', 'BAD', 'MissingClientName', N'test', N'{}', 'New');
                """, new { runId, source });
        }

        await using var read = new SqlConnection(sql.SyncHubConnectionString);
        var row = (await read.QueryAsync<ReconciliationRow>(
                "ops.usp_GetReconciliation", commandType: System.Data.CommandType.StoredProcedure))
            .Single(r => r.Source == source && r.Entity == Entities.Client);

        Assert.Equal((2, 1, 1, 0, 0), (row.SourceTotal, row.InHub, row.OpenExceptions, row.Ignored, row.Unexplained));
        Assert.Equal("ExplainedByExceptions", row.Status);
    }

    private sealed class ReconciliationRow
    {
        public string Source { get; init; } = "";
        public string Entity { get; init; } = "";
        public int SourceTotal { get; init; }
        public int InHub { get; init; }
        public int OpenExceptions { get; init; }
        public int Ignored { get; init; }
        public int Unexplained { get; init; }
        public string Status { get; init; } = "";
    }
}
