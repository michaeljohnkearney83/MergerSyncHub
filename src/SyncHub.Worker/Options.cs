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

    public int StagingRetentionDays { get; set; } = 7;
}

public sealed class FirmBOptions
{
    public const string Section = "FirmB";

    public string BaseUrl { get; set; } = "http://localhost:5070/";
    public string ApiKey { get; set; } = "";
    public int PageSize { get; set; } = 500;
}
