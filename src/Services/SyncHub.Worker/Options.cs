namespace SyncHub.Worker;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>How often a sync cycle runs.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Re-read this many minutes before the watermark each run. Catches rows that were
    /// committed late with an earlier timestamp. Safe because the merge is idempotent.
    /// </summary>
    public int LookbackMinutes { get; set; } = 5;

    /// <summary>Rows per SqlBulkCopy batch into staging.</summary>
    public int BatchSize { get; set; } = 5000;

    public double DuplicateThreshold { get; set; } = 0.92;

    public int ReportingRefreshMinutes { get; set; } = 5;

    /// <summary>
    /// Days of staging rows to keep. Staging is the largest table (every look-back re-stages rows), and
    /// replays read dead-letter payloads rather than staging, so two days is plenty for auditing a recent run.
    /// </summary>
    public int StagingRetentionDays { get; set; } = 2;

    /// <summary>Days of successful sync runs to keep in ops.SyncRun. Must be longer than StagingRetentionDays.</summary>
    public int RunHistorySucceededDays { get; set; } = 90;

    /// <summary>Days of failed and SucceededWithErrors runs to keep: they're what investigations look back at.</summary>
    public int RunHistoryOtherDays { get; set; } = 365;

    /// <summary>How often the SQL health snapshot behind the dead-letter and reconciliation gauges is read.</summary>
    public int HealthSnapshotMinutes { get; set; } = 2;
}

public sealed class FirmBOptions
{
    public const string Section = "FirmB";

    public string BaseUrl { get; set; } = "http://localhost:5070/";
    public string ApiKey { get; set; } = "";
    public int PageSize { get; set; } = 500;
}
