/*
    05_reporting.sql
    Reporting layer for SSRS and Power BI.

      rpt.DimDate, rpt.DimFirm, rpt.DimPracticeArea, rpt.DimClient
      rpt.FactMatter               - one row per matter
      rpt.FactDataQualityDaily     - one row per firm per day (trend of data quality)
      rpt.UserFirmAccess           - drives Power BI row-level security

    rpt.usp_LoadReporting rebuilds the star schema from core (a full reload is fine
    at this size; at larger volumes you would load incrementally using LastSyncRunId).

    No foreign keys in rpt: a common choice for reporting marts that are fully
    reloaded. Integrity is guaranteed by the load procedure instead.
*/
USE SyncHub;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DROP TABLE IF EXISTS rpt.FactMatter;
DROP TABLE IF EXISTS rpt.DimClient;
DROP TABLE IF EXISTS rpt.DimPracticeArea;
DROP TABLE IF EXISTS rpt.DimFirm;
DROP TABLE IF EXISTS rpt.DimDate;
DROP TABLE IF EXISTS rpt.UserFirmAccess;
GO

CREATE TABLE rpt.DimDate
(
    DateKey        int          NOT NULL CONSTRAINT PK_DimDate PRIMARY KEY,   -- yyyymmdd
    [Date]         date         NOT NULL,
    [Year]         smallint     NOT NULL,
    [Quarter]      tinyint      NOT NULL,
    [Month]        tinyint      NOT NULL,
    MonthName      varchar(10)  NOT NULL,
    YearMonth      char(7)      NOT NULL,   -- 2026-11
    FinancialYear  varchar(9)   NOT NULL,   -- UK style, April to March: FY2026/27
    IsWeekend      bit          NOT NULL
);

;WITH n AS (
    SELECT TOP (DATEDIFF(DAY, '20100101', '20301231') + 1)
           ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), d AS (
    SELECT DATEADD(DAY, i, CAST('20100101' AS date)) AS dt FROM n
)
INSERT rpt.DimDate
SELECT YEAR(dt) * 10000 + MONTH(dt) * 100 + DAY(dt),
       dt, YEAR(dt), DATEPART(QUARTER, dt), MONTH(dt),
       LEFT(DATENAME(MONTH, dt), 10),
       CONVERT(char(7), dt, 126),
       CASE WHEN MONTH(dt) >= 4
            THEN CONCAT('FY', YEAR(dt), '/', RIGHT(YEAR(dt) + 1, 2))
            ELSE CONCAT('FY', YEAR(dt) - 1, '/', RIGHT(YEAR(dt), 2)) END,
       CASE WHEN (DATEDIFF(DAY, '19000101', dt) % 7) IN (5, 6) THEN 1 ELSE 0 END   -- 1900-01-01 was a Monday
FROM d;

CREATE TABLE rpt.DimFirm
(
    FirmKey   tinyint       NOT NULL CONSTRAINT PK_DimFirm PRIMARY KEY,
    Source    varchar(20)   NOT NULL CONSTRAINT UX_DimFirm_Source UNIQUE,
    FirmName  nvarchar(100) NOT NULL,
    Region    nvarchar(50)  NOT NULL
);
INSERT rpt.DimFirm VALUES
 (1, 'FirmA', N'Firm A (legacy UK firm)', N'UK'),
 (2, 'FirmB', N'Firm B (legacy US firm)', N'US');

CREATE TABLE rpt.DimPracticeArea
(
    PracticeAreaKey int IDENTITY(1,1) NOT NULL CONSTRAINT PK_DimPracticeArea PRIMARY KEY,
    PracticeArea    nvarchar(100) NOT NULL CONSTRAINT UX_DimPracticeArea UNIQUE
);

CREATE TABLE rpt.DimClient
(
    ClientKey           int           NOT NULL CONSTRAINT PK_DimClient PRIMARY KEY,
    Source              varchar(20)   NOT NULL,
    ClientName          nvarchar(200) NOT NULL,
    Country             nvarchar(60)  NULL,
    PostCode            nvarchar(12)  NULL,
    IsActive            bit           NOT NULL,
    MasterClientKey     int           NOT NULL,   -- same value for all confirmed duplicates
    MasterClientName    nvarchar(200) NOT NULL,
    IsInDuplicateGroup  bit           NOT NULL,
    GroupSpansFirms     bit           NOT NULL    -- the client was a client of both legacy firms
);

CREATE TABLE rpt.FactMatter
(
    MatterKey        int          NOT NULL CONSTRAINT PK_FactMatter PRIMARY KEY,
    ClientKey        int          NOT NULL,
    MasterClientKey  int          NOT NULL,
    FirmKey          tinyint      NOT NULL,
    PracticeAreaKey  int          NOT NULL,
    PracticeAreaCode varchar(30)  NULL,
    MatterNumber     nvarchar(30) NOT NULL,
    FeeEarner        nvarchar(50) NULL,
    OpenedDateKey    int          NULL,
    ClosedDateKey    int          NULL,
    IsOpen           bit          NOT NULL,
    DaysOpen         int          NULL
);
CREATE INDEX IX_FactMatter_Client ON rpt.FactMatter (ClientKey);

-- The one rpt table that can't be rebuilt from core: it is a daily history. Create it only
-- if missing, so re-running this script (to change a procedure, say) keeps the trend.
-- FirmKey values are fixed in DimFirm above, so old rows still join after a reload.
IF OBJECT_ID(N'rpt.FactDataQualityDaily', N'U') IS NULL
CREATE TABLE rpt.FactDataQualityDaily
(
    SnapshotDate             date    NOT NULL,
    FirmKey                  tinyint NOT NULL,
    TotalClients             int     NOT NULL,
    TotalMatters             int     NOT NULL,
    OpenExceptions           int     NOT NULL,
    PendingDuplicateReviews  int     NOT NULL,
    ConfirmedDuplicates      int     NOT NULL,
    UnmappedMatters          int     NOT NULL,
    CONSTRAINT PK_FactDataQualityDaily PRIMARY KEY (SnapshotDate, FirmKey)
);

CREATE TABLE rpt.UserFirmAccess
(
    UserPrincipalName nvarchar(256) NOT NULL,
    Source            varchar(20)   NOT NULL,
    CONSTRAINT PK_UserFirmAccess PRIMARY KEY (UserPrincipalName, Source)
);
-- Replace with real UPNs. In Power BI Desktop use Modeling > View as > Other user to test.
INSERT rpt.UserFirmAccess VALUES
 (N'uk.partner@example.com', 'FirmA'),
 (N'us.partner@example.com', 'FirmB'),
 (N'coo@example.com',        'FirmA'),
 (N'coo@example.com',        'FirmB');
GO

/* ===========================================================================
   Load
=========================================================================== */
CREATE OR ALTER PROCEDURE rpt.usp_LoadReporting
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* 1. Group confirmed duplicates into one "master" client.
          Label propagation: every client starts with its own id as label, and
          repeatedly takes the smallest label of its neighbours until nothing changes.
          This handles chains (A=B, B=C means A, B and C are one client). */
    CREATE TABLE #label (ClientId int NOT NULL PRIMARY KEY, Label int NOT NULL);
    INSERT #label SELECT ClientId, ClientId FROM core.Client;

    CREATE TABLE #edge (a int NOT NULL, b int NOT NULL);
    INSERT #edge
    SELECT ClientIdA, ClientIdB FROM core.ClientMatchCandidate WHERE Status = 'ConfirmedDuplicate'
    UNION ALL
    SELECT ClientIdB, ClientIdA FROM core.ClientMatchCandidate WHERE Status = 'ConfirmedDuplicate';

    WHILE 1 = 1
    BEGIN
        UPDATE l
           SET Label = m.MinLabel
          FROM #label l
          JOIN (SELECT e.a, MIN(l2.Label) AS MinLabel
                  FROM #edge e JOIN #label l2 ON l2.ClientId = e.b
                 GROUP BY e.a) m ON m.a = l.ClientId
         WHERE m.MinLabel < l.Label;
        IF @@ROWCOUNT = 0 BREAK;
    END;

    CREATE TABLE #group (Label int NOT NULL PRIMARY KEY, Members int NOT NULL, Firms int NOT NULL);
    INSERT #group
    SELECT l.Label, COUNT(*), COUNT(DISTINCT c.Source)
      FROM #label l JOIN core.Client c ON c.ClientId = l.ClientId
     GROUP BY l.Label;

    BEGIN TRAN;

    TRUNCATE TABLE rpt.FactMatter;
    TRUNCATE TABLE rpt.DimClient;
    TRUNCATE TABLE rpt.DimPracticeArea;

    INSERT rpt.DimPracticeArea (PracticeArea)
    SELECT DISTINCT PracticeArea FROM core.PracticeAreaMap
    UNION
    SELECT N'Unmapped';

    INSERT rpt.DimClient (ClientKey, Source, ClientName, Country, PostCode, IsActive,
                          MasterClientKey, MasterClientName, IsInDuplicateGroup, GroupSpansFirms)
    SELECT c.ClientId, c.Source, c.ClientName, c.Country, c.PostCode, c.IsActive,
           l.Label, mc.ClientName,
           CASE WHEN g.Members > 1 THEN 1 ELSE 0 END,
           CASE WHEN g.Firms > 1 THEN 1 ELSE 0 END
      FROM core.Client c
      JOIN #label l      ON l.ClientId = c.ClientId
      JOIN #group g      ON g.Label = l.Label
      JOIN core.Client mc ON mc.ClientId = l.Label;

    INSERT rpt.FactMatter (MatterKey, ClientKey, MasterClientKey, FirmKey, PracticeAreaKey, PracticeAreaCode,
                           MatterNumber, FeeEarner, OpenedDateKey, ClosedDateKey, IsOpen, DaysOpen)
    SELECT m.MatterId, m.ClientId, l.Label, f.FirmKey, pa.PracticeAreaKey, m.PracticeAreaCode,
           m.MatterNumber, m.FeeEarner,
           YEAR(m.OpenedOn) * 10000 + MONTH(m.OpenedOn) * 100 + DAY(m.OpenedOn),
           YEAR(m.ClosedOn) * 10000 + MONTH(m.ClosedOn) * 100 + DAY(m.ClosedOn),
           m.IsOpen,
           DATEDIFF(DAY, m.OpenedOn, COALESCE(m.ClosedOn, CAST(SYSUTCDATETIME() AS date)))
      FROM core.Matter m
      JOIN #label l      ON l.ClientId = m.ClientId
      JOIN rpt.DimFirm f ON f.Source = m.Source
      LEFT JOIN core.PracticeAreaMap pm ON pm.Source = m.Source AND pm.SourceCode = m.PracticeAreaCode
      JOIN rpt.DimPracticeArea pa ON pa.PracticeArea = ISNULL(pm.PracticeArea, N'Unmapped');

    /* 2. Today's data-quality snapshot, one row per firm (re-running today overwrites) */
    DECLARE @Today date = CAST(SYSUTCDATETIME() AS date);

    MERGE rpt.FactDataQualityDaily AS t
    USING (
        SELECT @Today AS SnapshotDate, f.FirmKey,
               (SELECT COUNT(*) FROM core.Client c WHERE c.Source = f.Source) AS TotalClients,
               (SELECT COUNT(*) FROM core.Matter m WHERE m.Source = f.Source) AS TotalMatters,
               (SELECT COUNT(*) FROM ops.DeadLetter d WHERE d.Source = f.Source AND d.Status IN ('New','ReplayRequested')) AS OpenExceptions,
               (SELECT COUNT(*) FROM core.ClientMatchCandidate mc JOIN core.Client c ON c.ClientId = mc.ClientIdA
                 WHERE c.Source = f.Source AND mc.Status = 'Pending') AS PendingDuplicateReviews,
               (SELECT COUNT(*) FROM core.ClientMatchCandidate mc JOIN core.Client c ON c.ClientId = mc.ClientIdA
                 WHERE c.Source = f.Source AND mc.Status = 'ConfirmedDuplicate') AS ConfirmedDuplicates,
               (SELECT COUNT(*) FROM rpt.FactMatter fm JOIN rpt.DimPracticeArea pa ON pa.PracticeAreaKey = fm.PracticeAreaKey
                 WHERE fm.FirmKey = f.FirmKey AND pa.PracticeArea = N'Unmapped') AS UnmappedMatters
          FROM rpt.DimFirm f
    ) AS s
       ON t.SnapshotDate = s.SnapshotDate AND t.FirmKey = s.FirmKey
    WHEN MATCHED THEN UPDATE SET
         TotalClients = s.TotalClients, TotalMatters = s.TotalMatters, OpenExceptions = s.OpenExceptions,
         PendingDuplicateReviews = s.PendingDuplicateReviews, ConfirmedDuplicates = s.ConfirmedDuplicates,
         UnmappedMatters = s.UnmappedMatters
    WHEN NOT MATCHED THEN INSERT
         (SnapshotDate, FirmKey, TotalClients, TotalMatters, OpenExceptions, PendingDuplicateReviews, ConfirmedDuplicates, UnmappedMatters)
         VALUES (s.SnapshotDate, s.FirmKey, s.TotalClients, s.TotalMatters, s.OpenExceptions, s.PendingDuplicateReviews, s.ConfirmedDuplicates, s.UnmappedMatters);

    COMMIT;
END;
GO

/* ===========================================================================
   SSRS datasets
   SSRS works best with stored procedures that take simple parameters.
   'All' is used instead of a multi-value parameter to keep the procs simple.
=========================================================================== */
CREATE OR ALTER PROCEDURE rpt.usp_SsrsExceptions
    @Source varchar(20) = 'All',
    @Status varchar(20) = 'Open'     -- Open | Resolved | Ignored | All
AS
BEGIN
    SET NOCOUNT ON;
    SELECT d.DeadLetterId,
           d.Source,
           d.Entity,
           d.SourceKey,
           d.ErrorType,
           d.ErrorMessage,
           d.Status,
           d.OccurrenceCount,
           d.ReplayAttempts,
           d.CreatedAtUtc,
           d.LastSeenAtUtc,
           AgeHours = DATEDIFF(HOUR, d.CreatedAtUtc, SYSUTCDATETIME()),
           AgeBand  = CASE WHEN DATEDIFF(HOUR, d.CreatedAtUtc, SYSUTCDATETIME()) < 24  THEN '< 1 day'
                           WHEN DATEDIFF(HOUR, d.CreatedAtUtc, SYSUTCDATETIME()) < 72  THEN '1-3 days'
                           ELSE '3+ days' END,
           d.ResolutionNote
      FROM ops.DeadLetter d
     WHERE (@Source = 'All' OR d.Source = @Source)
       AND (   @Status = 'All'
            OR (@Status = 'Open' AND d.Status IN ('New','ReplayRequested'))
            OR d.Status = @Status)
     ORDER BY d.Source, d.ErrorType, d.CreatedAtUtc;
END;
GO

CREATE OR ALTER PROCEDURE rpt.usp_SsrsSyncRunSummary
    @Days int = 7
AS
BEGIN
    SET NOCOUNT ON;
    SELECT r.SyncRunId, r.Source, r.RunType, r.Status, r.StartedAtUtc, r.FinishedAtUtc,
           DurationSeconds = DATEDIFF(SECOND, r.StartedAtUtc, r.FinishedAtUtc),
           e.Entity, e.SourceTotal, e.RecordsRead, e.RecordsInserted, e.RecordsUpdated, e.RecordsFailed,
           r.ErrorMessage, r.CorrelationId
      FROM ops.SyncRun r
      LEFT JOIN ops.SyncRunEntity e ON e.SyncRunId = r.SyncRunId
     WHERE r.StartedAtUtc >= DATEADD(DAY, -@Days, SYSUTCDATETIME())
     ORDER BY r.SyncRunId DESC, e.Entity;
END;
GO
