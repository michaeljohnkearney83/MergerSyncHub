using Dapper;
using Microsoft.Data.SqlClient;

namespace SyncHub.IntegrationTests;

/// <summary>
/// Data lifetimes: what gets purged, what must survive, and that the calendar never runs out.
/// These run against the real procedures, because a wrong WHERE clause in a purge deletes real history.
/// </summary>
[Collection("sql")]
public sealed class RetentionTests(SqlServerFixture sql)
{
    private SqlConnection Open() => new(sql.SyncHubConnectionString);

    /// <summary>Inserts a run that started <paramref name="daysAgo"/> days ago, with one entity row.</summary>
    private async Task<long> InsertRunAsync(string source, string status, int daysAgo)
    {
        await using var conn = Open();
        var id = await conn.QuerySingleAsync<long>("""
            INSERT ops.SyncRun (Source, RunType, CorrelationId, StartedAtUtc, FinishedAtUtc, Status)
            OUTPUT inserted.SyncRunId
            VALUES (@source, 'Scheduled', NEWID(), DATEADD(DAY, -@daysAgo, SYSUTCDATETIME()),
                    CASE WHEN @status = 'Running' THEN NULL ELSE DATEADD(DAY, -@daysAgo, SYSUTCDATETIME()) END, @status);
            """, new { source, status, daysAgo });
        await conn.ExecuteAsync("INSERT ops.SyncRunEntity (SyncRunId, Entity) VALUES (@id, 'Client')", new { id });
        return id;
    }

    [Fact]
    public async Task Run_history_purge_keeps_failures_longer_and_never_touches_running_runs()
    {
        var source = SqlServerFixture.NewSource();
        var oldSuccess    = await InsertRunAsync(source, "Succeeded", daysAgo: 100);   // past 90: purge
        var recentSuccess = await InsertRunAsync(source, "Succeeded", daysAgo: 30);    // keep
        var oldFailure    = await InsertRunAsync(source, "Failed", daysAgo: 100);      // within 365: keep
        var ancientError  = await InsertRunAsync(source, "SucceededWithErrors", daysAgo: 400); // past 365: purge
        var stuckRun      = await InsertRunAsync(source, "Running", daysAgo: 400);     // never purged

        await using var conn = Open();
        await conn.ExecuteAsync("ops.usp_PurgeRunHistory", new { KeepSucceededDays = 90, KeepOtherDays = 365 },
            commandType: System.Data.CommandType.StoredProcedure);

        var remaining = (await conn.QueryAsync<long>(
            "SELECT SyncRunId FROM ops.SyncRun WHERE Source = @source", new { source })).ToHashSet();
        Assert.Equal(new HashSet<long> { recentSuccess, oldFailure, stuckRun }, remaining);

        // Child rows go with their run (and the FK would have stopped the purge otherwise).
        var orphanedEntities = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM ops.SyncRunEntity WHERE SyncRunId IN @ids", new { ids = new[] { oldSuccess, ancientError } });
        Assert.Equal(0, orphanedEntities);
    }

    [Fact]
    public async Task Calendar_extends_without_gaps_and_is_safe_to_run_again()
    {
        await using var conn = Open();
        await conn.ExecuteAsync("rpt.usp_ExtendDimDate", new { Through = new DateTime(2032, 12, 31) },
            commandType: System.Data.CommandType.StoredProcedure);
        await conn.ExecuteAsync("rpt.usp_ExtendDimDate", new { Through = new DateTime(2032, 12, 31) },
            commandType: System.Data.CommandType.StoredProcedure);   // second run adds nothing

        var (first, last, days) = await conn.QuerySingleAsync<(DateTime, DateTime, int)>(
            "SELECT MIN([Date]), MAX([Date]), COUNT(*) FROM rpt.DimDate");
        Assert.Equal(new DateTime(2010, 1, 1), first);
        Assert.True(last >= new DateTime(2032, 12, 31));
        Assert.Equal((last - first).Days + 1, days);   // one row per day: no gaps, no duplicates

        // A date past the old fixed end (31 Dec 2030) gets the right keys and UK financial year.
        var (dateKey, financialYear) = await conn.QuerySingleAsync<(int, string)>(
            "SELECT DateKey, FinancialYear FROM rpt.DimDate WHERE [Date] = '20310401'");
        Assert.Equal(20310401, dateKey);
        Assert.Equal("FY2031/32", financialYear);
    }

    [Fact]
    public async Task By_default_the_calendar_reaches_the_end_of_the_year_two_years_ahead()
    {
        await using var conn = Open();
        await conn.ExecuteAsync("rpt.usp_ExtendDimDate", commandType: System.Data.CommandType.StoredProcedure);
        var last = await conn.QuerySingleAsync<DateTime>("SELECT MAX([Date]) FROM rpt.DimDate");
        Assert.True(last >= new DateTime(DateTime.UtcNow.Year + 2, 12, 31));
    }

    [Fact]
    public async Task Hub_database_uses_simple_recovery_so_the_log_cannot_grow_without_limit()
    {
        await using var conn = Open();
        var model = await conn.QuerySingleAsync<string>(
            "SELECT recovery_model_desc FROM sys.databases WHERE name = 'SyncHub'");
        Assert.Equal("SIMPLE", model);
    }
}
