/*
    04_synchub_procedures.sql
    Stored procedures used by the worker and the support API.

    Design points worth talking about:
      * Merges are set-based (MERGE from a de-duplicated staging set), not row by row.
      * Merges are idempotent: re-running the same batch changes nothing, because
        updates only happen when the row hash differs. That makes retries safe.
      * An older version of a record never overwrites a newer one
        (SourceModifiedUtc check), which matters when replaying old failures.
      * Failures go to ops.DeadLetter with the raw payload, and resolve themselves
        automatically when the record later merges successfully.
*/
USE SyncHub;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ===========================================================================
   Run tracking and watermarks
=========================================================================== */
CREATE OR ALTER PROCEDURE ops.usp_StartSyncRun
    @Source        varchar(20),
    @RunType       varchar(20),
    @CorrelationId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    INSERT ops.SyncRun (Source, RunType, CorrelationId) VALUES (@Source, @RunType, @CorrelationId);
    SELECT CAST(SCOPE_IDENTITY() AS bigint) AS SyncRunId;
END;
GO

CREATE OR ALTER PROCEDURE ops.usp_CompleteSyncRun
    @SyncRunId    bigint,
    @Status       varchar(30),
    @ErrorMessage nvarchar(4000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE ops.SyncRun
       SET Status = @Status, ErrorMessage = @ErrorMessage, FinishedAtUtc = SYSUTCDATETIME()
     WHERE SyncRunId = @SyncRunId;
END;
GO

CREATE OR ALTER PROCEDURE ops.usp_SaveSyncRunEntity
    @SyncRunId bigint, @Entity varchar(20), @SourceTotal int = NULL,
    @RecordsRead int, @RecordsStaged int, @RecordsInserted int, @RecordsUpdated int, @RecordsFailed int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE ops.SyncRunEntity
       SET SourceTotal = @SourceTotal, RecordsRead = @RecordsRead, RecordsStaged = @RecordsStaged,
           RecordsInserted = @RecordsInserted, RecordsUpdated = @RecordsUpdated, RecordsFailed = @RecordsFailed
     WHERE SyncRunId = @SyncRunId AND Entity = @Entity;

    IF @@ROWCOUNT = 0
        INSERT ops.SyncRunEntity (SyncRunId, Entity, SourceTotal, RecordsRead, RecordsStaged, RecordsInserted, RecordsUpdated, RecordsFailed)
        VALUES (@SyncRunId, @Entity, @SourceTotal, @RecordsRead, @RecordsStaged, @RecordsInserted, @RecordsUpdated, @RecordsFailed);
END;
GO

CREATE OR ALTER PROCEDURE ops.usp_GetWatermark
    @Source varchar(20), @Entity varchar(20)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT LastModified FROM ops.Watermark WHERE Source = @Source AND Entity = @Entity;
END;
GO

/* Only ever moves forward */
CREATE OR ALTER PROCEDURE ops.usp_SetWatermark
    @Source varchar(20), @Entity varchar(20), @LastModified datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE ops.Watermark
       SET LastModified = CASE WHEN @LastModified > LastModified THEN @LastModified ELSE LastModified END,
           UpdatedAtUtc = SYSUTCDATETIME()
     WHERE Source = @Source AND Entity = @Entity;

    IF @@ROWCOUNT = 0
        INSERT ops.Watermark (Source, Entity, LastModified) VALUES (@Source, @Entity, @LastModified);
END;
GO

/* ===========================================================================
   Dead letters
=========================================================================== */
CREATE OR ALTER PROCEDURE ops.usp_UpsertDeadLetter
    @SyncRunId        bigint,
    @Source           varchar(20),
    @Entity           varchar(20),
    @SourceKey        varchar(50),
    @ErrorType        varchar(50),
    @ErrorMessage     nvarchar(2000),
    @Payload          nvarchar(max),
    @RelatedClientKey varchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- If this record already has an open dead letter, update it rather than adding another
    UPDATE ops.DeadLetter
       SET LastSyncRunId   = @SyncRunId,
           LastSeenAtUtc   = SYSUTCDATETIME(),
           OccurrenceCount = OccurrenceCount + 1,
           ReplayAttempts  = ReplayAttempts + CASE WHEN Status = 'ReplayRequested' THEN 1 ELSE 0 END,
           Status          = 'New',
           ErrorType       = @ErrorType,
           ErrorMessage    = @ErrorMessage,
           Payload         = @Payload,
           RelatedClientKey = @RelatedClientKey
     WHERE Source = @Source AND Entity = @Entity AND SourceKey = @SourceKey
       AND Status IN ('New', 'ReplayRequested');

    IF @@ROWCOUNT = 0
        INSERT ops.DeadLetter (FirstSyncRunId, LastSyncRunId, Source, Entity, SourceKey, RelatedClientKey, ErrorType, ErrorMessage, Payload)
        VALUES (@SyncRunId, @SyncRunId, @Source, @Entity, @SourceKey, @RelatedClientKey, @ErrorType, @ErrorMessage, @Payload);
END;
GO

CREATE OR ALTER PROCEDURE ops.usp_GetReplayQueue
AS
BEGIN
    SET NOCOUNT ON;
    -- Clients first, so orphan matters waiting on them can succeed in the same replay
    SELECT DeadLetterId, Source, Entity, SourceKey, Payload
      FROM ops.DeadLetter
     WHERE Status = 'ReplayRequested'
     ORDER BY Source, CASE Entity WHEN 'Client' THEN 0 ELSE 1 END, DeadLetterId;
END;
GO

/* ===========================================================================
   Merge: clients
=========================================================================== */
CREATE OR ALTER PROCEDURE core.usp_MergeClients
    @SyncRunId bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    CREATE TABLE #src
    (
        Source varchar(20) NOT NULL, SourceClientKey varchar(50) NOT NULL,
        ClientName nvarchar(200) NOT NULL, NormalisedName nvarchar(200) NOT NULL,
        Country nvarchar(60) NULL, PostCode nvarchar(12) NULL, OpenedOn date NULL, IsActive bit NOT NULL,
        SourceModifiedUtc datetime2(3) NOT NULL, RowHash binary(32) NOT NULL,
        PRIMARY KEY (Source, SourceClientKey)
    );

    -- The same record can be staged twice in one run (overlap window, or edited mid-read).
    -- MERGE fails if the source has duplicate keys, so keep only the latest version.
    INSERT #src
    SELECT d.Source, d.SourceClientKey, d.ClientName, d.NormalisedName, d.Country, d.PostCode, d.OpenedOn, d.IsActive,
           d.SourceModifiedUtc,
           HASHBYTES('SHA2_256', CONCAT(d.ClientName, N'|', ISNULL(d.Country, N'~'), N'|', ISNULL(d.PostCode, N'~'), N'|',
                                        ISNULL(CONVERT(char(10), d.OpenedOn, 23), '~'), N'|', d.IsActive))
    FROM (
        SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.Source, s.SourceClientKey
                                       ORDER BY s.SourceModifiedUtc DESC, s.StagedClientId DESC) AS rn
        FROM stg.Client s
        WHERE s.SyncRunId = @SyncRunId
    ) d
    WHERE d.rn = 1;

    DECLARE @Changes TABLE (Action nvarchar(10), Source varchar(20), SourceClientKey varchar(50));

    BEGIN TRAN;

    MERGE core.Client WITH (HOLDLOCK) AS t
    USING #src AS s
       ON t.Source = s.Source AND t.SourceClientKey = s.SourceClientKey
    WHEN MATCHED AND t.RowHash <> s.RowHash AND s.SourceModifiedUtc >= t.SourceModifiedUtc THEN
        UPDATE SET ClientName = s.ClientName, NormalisedName = s.NormalisedName, Country = s.Country,
                   PostCode = s.PostCode, OpenedOn = s.OpenedOn, IsActive = s.IsActive,
                   SourceModifiedUtc = s.SourceModifiedUtc, RowHash = s.RowHash,
                   UpdatedAtUtc = SYSUTCDATETIME(), LastSyncRunId = @SyncRunId
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Source, SourceClientKey, ClientName, NormalisedName, Country, PostCode, OpenedOn, IsActive,
                SourceModifiedUtc, RowHash, LastSyncRunId)
        VALUES (s.Source, s.SourceClientKey, s.ClientName, s.NormalisedName, s.Country, s.PostCode, s.OpenedOn, s.IsActive,
                s.SourceModifiedUtc, s.RowHash, @SyncRunId)
    OUTPUT $action, inserted.Source, inserted.SourceClientKey INTO @Changes;

    -- Records that failed before but came through cleanly this time
    UPDATE dl
       SET Status = 'Resolved', ResolvedAtUtc = SYSUTCDATETIME(),
           ResolutionNote = LEFT(CONCAT(dl.ResolutionNote + N' | ', N'Auto-resolved: merged in sync run ', @SyncRunId), 1000)
      FROM ops.DeadLetter dl
      JOIN #src s ON dl.Source = s.Source AND dl.Entity = 'Client' AND dl.SourceKey = s.SourceClientKey
     WHERE dl.Status IN ('New', 'ReplayRequested');

    -- Orphan matters that were waiting for a client that now exists: queue them for replay
    UPDATE dl
       SET Status = 'ReplayRequested',
           ResolutionNote = LEFT(CONCAT(dl.ResolutionNote + N' | ', N'Client arrived in sync run ', @SyncRunId, N'; queued for replay'), 1000)
      FROM ops.DeadLetter dl
      JOIN @Changes c ON c.Action = 'INSERT' AND dl.Source = c.Source AND dl.RelatedClientKey = c.SourceClientKey
     WHERE dl.Entity = 'Matter' AND dl.ErrorType = 'OrphanMatter' AND dl.Status = 'New';

    COMMIT;

    SELECT Inserted = ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0),
           Updated  = ISNULL(SUM(CASE WHEN Action = 'UPDATE' THEN 1 ELSE 0 END), 0),
           Failed   = 0
      FROM @Changes;
END;
GO

/* ===========================================================================
   Merge: matters
=========================================================================== */
CREATE OR ALTER PROCEDURE core.usp_MergeMatters
    @SyncRunId bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    CREATE TABLE #src
    (
        Source varchar(20) NOT NULL, SourceMatterKey varchar(50) NOT NULL, SourceClientKey varchar(50) NOT NULL,
        ClientId int NULL, MatterNumber nvarchar(30) NOT NULL, Description nvarchar(500) NULL,
        PracticeAreaCode varchar(30) NULL, FeeEarner nvarchar(50) NULL, OpenedOn date NULL, ClosedOn date NULL,
        IsOpen bit NOT NULL, SourceModifiedUtc datetime2(3) NOT NULL, RawPayload nvarchar(max) NOT NULL,
        RowHash binary(32) NOT NULL,
        PRIMARY KEY (Source, SourceMatterKey)
    );

    INSERT #src
    SELECT d.Source, d.SourceMatterKey, d.SourceClientKey, c.ClientId, d.MatterNumber, d.Description,
           d.PracticeAreaCode, d.FeeEarner, d.OpenedOn, d.ClosedOn, d.IsOpen, d.SourceModifiedUtc, d.RawPayload,
           HASHBYTES('SHA2_256', CONCAT(d.SourceClientKey, N'|', d.MatterNumber, N'|', ISNULL(d.Description, N'~'), N'|',
                                        ISNULL(d.PracticeAreaCode, '~'), N'|', ISNULL(d.FeeEarner, N'~'), N'|',
                                        ISNULL(CONVERT(char(10), d.OpenedOn, 23), '~'), N'|',
                                        ISNULL(CONVERT(char(10), d.ClosedOn, 23), '~'), N'|', d.IsOpen))
    FROM (
        SELECT s.*, ROW_NUMBER() OVER (PARTITION BY s.Source, s.SourceMatterKey
                                       ORDER BY s.SourceModifiedUtc DESC, s.StagedMatterId DESC) AS rn
        FROM stg.Matter s
        WHERE s.SyncRunId = @SyncRunId
    ) d
    LEFT JOIN core.Client c ON c.Source = d.Source AND c.SourceClientKey = d.SourceClientKey
    WHERE d.rn = 1;

    DECLARE @Changes TABLE (Action nvarchar(10));
    DECLARE @Orphans int = (SELECT COUNT(*) FROM #src WHERE ClientId IS NULL);

    BEGIN TRAN;

    /* Orphans: the client isn't in the hub (yet). Dead-letter them rather than fail the batch. */
    UPDATE dl
       SET LastSyncRunId = @SyncRunId, LastSeenAtUtc = SYSUTCDATETIME(),
           OccurrenceCount = dl.OccurrenceCount + 1,
           ReplayAttempts = dl.ReplayAttempts + CASE WHEN dl.Status = 'ReplayRequested' THEN 1 ELSE 0 END,
           Status = 'New', ErrorType = 'OrphanMatter',
           ErrorMessage = CONCAT(N'Client ', s.SourceClientKey, N' from ', s.Source, N' is not in the hub. Fix or sync the client; matters are re-queued automatically when it arrives.'),
           Payload = s.RawPayload, RelatedClientKey = s.SourceClientKey
      FROM ops.DeadLetter dl
      JOIN #src s ON dl.Source = s.Source AND dl.Entity = 'Matter' AND dl.SourceKey = s.SourceMatterKey
     WHERE s.ClientId IS NULL AND dl.Status IN ('New', 'ReplayRequested');

    INSERT ops.DeadLetter (FirstSyncRunId, LastSyncRunId, Source, Entity, SourceKey, RelatedClientKey, ErrorType, ErrorMessage, Payload)
    SELECT @SyncRunId, @SyncRunId, s.Source, 'Matter', s.SourceMatterKey, s.SourceClientKey, 'OrphanMatter',
           CONCAT(N'Client ', s.SourceClientKey, N' from ', s.Source, N' is not in the hub. Fix or sync the client; matters are re-queued automatically when it arrives.'),
           s.RawPayload
      FROM #src s
     WHERE s.ClientId IS NULL
       AND NOT EXISTS (SELECT 1 FROM ops.DeadLetter dl
                        WHERE dl.Source = s.Source AND dl.Entity = 'Matter' AND dl.SourceKey = s.SourceMatterKey
                          AND dl.Status IN ('New', 'ReplayRequested'));

    MERGE core.Matter WITH (HOLDLOCK) AS t
    USING (SELECT * FROM #src WHERE ClientId IS NOT NULL) AS s
       ON t.Source = s.Source AND t.SourceMatterKey = s.SourceMatterKey
    WHEN MATCHED AND t.RowHash <> s.RowHash AND s.SourceModifiedUtc >= t.SourceModifiedUtc THEN
        UPDATE SET ClientId = s.ClientId, MatterNumber = s.MatterNumber, Description = s.Description,
                   PracticeAreaCode = s.PracticeAreaCode, FeeEarner = s.FeeEarner, OpenedOn = s.OpenedOn,
                   ClosedOn = s.ClosedOn, IsOpen = s.IsOpen, SourceModifiedUtc = s.SourceModifiedUtc,
                   RowHash = s.RowHash, UpdatedAtUtc = SYSUTCDATETIME(), LastSyncRunId = @SyncRunId
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Source, SourceMatterKey, ClientId, MatterNumber, Description, PracticeAreaCode, FeeEarner,
                OpenedOn, ClosedOn, IsOpen, SourceModifiedUtc, RowHash, LastSyncRunId)
        VALUES (s.Source, s.SourceMatterKey, s.ClientId, s.MatterNumber, s.Description, s.PracticeAreaCode, s.FeeEarner,
                s.OpenedOn, s.ClosedOn, s.IsOpen, s.SourceModifiedUtc, s.RowHash, @SyncRunId)
    OUTPUT $action INTO @Changes;

    UPDATE dl
       SET Status = 'Resolved', ResolvedAtUtc = SYSUTCDATETIME(),
           ResolutionNote = LEFT(CONCAT(dl.ResolutionNote + N' | ', N'Auto-resolved: merged in sync run ', @SyncRunId), 1000)
      FROM ops.DeadLetter dl
      JOIN #src s ON dl.Source = s.Source AND dl.Entity = 'Matter' AND dl.SourceKey = s.SourceMatterKey
     WHERE s.ClientId IS NOT NULL AND dl.Status IN ('New', 'ReplayRequested');

    COMMIT;

    SELECT Inserted = ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0),
           Updated  = ISNULL(SUM(CASE WHEN Action = 'UPDATE' THEN 1 ELSE 0 END), 0),
           Failed   = @Orphans
      FROM @Changes;
END;
GO

/* ===========================================================================
   Duplicate client candidates (scored in C#, saved set-based via a TVP)
=========================================================================== */
CREATE OR ALTER PROCEDURE core.usp_GetClientsForMatching
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ClientId, Source, NormalisedName, PostCode FROM core.Client;
END;
GO

CREATE OR ALTER PROCEDURE core.usp_SaveMatchCandidates
    @Candidates core.MatchCandidateList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Changes TABLE (Action nvarchar(10));

    -- Reviewed pairs are left alone: a person's decision beats the algorithm
    MERGE core.ClientMatchCandidate AS t
    USING @Candidates AS s
       ON t.ClientIdA = s.ClientIdA AND t.ClientIdB = s.ClientIdB
    WHEN MATCHED AND t.Status = 'Pending' THEN
        UPDATE SET Score = s.Score, MatchReason = s.MatchReason, LastScoredAtUtc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ClientIdA, ClientIdB, Score, MatchReason) VALUES (s.ClientIdA, s.ClientIdB, s.Score, s.MatchReason)
    OUTPUT $action INTO @Changes;

    SELECT NewCandidates = ISNULL(SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END), 0) FROM @Changes;
END;
GO

/* ===========================================================================
   Reconciliation: does what the sources hold match what the hub holds?
   SourceTotal = InHub + OpenExceptions + Ignored, otherwise something is unexplained.
   Exceptions for records that already exist in the hub (a failed *update*) are not
   counted, as they do not explain a missing row.
=========================================================================== */
CREATE OR ALTER PROCEDURE ops.usp_GetReconciliation
AS
BEGIN
    SET NOCOUNT ON;

    WITH latest AS (
        SELECT r.Source, e.Entity, e.SourceTotal, r.SyncRunId, r.StartedAtUtc,
               ROW_NUMBER() OVER (PARTITION BY r.Source, e.Entity ORDER BY r.SyncRunId DESC) AS rn
          FROM ops.SyncRun r
          JOIN ops.SyncRunEntity e ON e.SyncRunId = r.SyncRunId
         WHERE r.RunType = 'Scheduled' AND e.SourceTotal IS NOT NULL
    ),
    hub AS (
        SELECT Source, CAST('Client' AS varchar(20)) AS Entity, COUNT(*) AS Cnt FROM core.Client GROUP BY Source
        UNION ALL
        SELECT Source, 'Matter', COUNT(*) FROM core.Matter GROUP BY Source
    ),
    exceptions AS (
        SELECT dl.Source, dl.Entity,
               SUM(CASE WHEN dl.Status IN ('New','ReplayRequested') THEN 1 ELSE 0 END) AS OpenCnt,
               SUM(CASE WHEN dl.Status = 'Ignored' THEN 1 ELSE 0 END) AS IgnoredCnt
          FROM ops.DeadLetter dl
         WHERE dl.Status IN ('New','ReplayRequested','Ignored')
           AND NOT EXISTS (SELECT 1 FROM core.Client c WHERE dl.Entity = 'Client' AND c.Source = dl.Source AND c.SourceClientKey = dl.SourceKey)
           AND NOT EXISTS (SELECT 1 FROM core.Matter m WHERE dl.Entity = 'Matter' AND m.Source = dl.Source AND m.SourceMatterKey = dl.SourceKey)
         GROUP BY dl.Source, dl.Entity
    )
    SELECT l.Source,
           l.Entity,
           l.SourceTotal,
           InHub          = ISNULL(h.Cnt, 0),
           OpenExceptions = ISNULL(x.OpenCnt, 0),
           Ignored        = ISNULL(x.IgnoredCnt, 0),
           Unexplained    = l.SourceTotal - ISNULL(h.Cnt, 0) - ISNULL(x.OpenCnt, 0) - ISNULL(x.IgnoredCnt, 0),
           Status         = CASE
                                WHEN l.SourceTotal - ISNULL(h.Cnt, 0) - ISNULL(x.OpenCnt, 0) - ISNULL(x.IgnoredCnt, 0) <> 0 THEN 'Investigate'
                                WHEN ISNULL(x.OpenCnt, 0) > 0 THEN 'ExplainedByExceptions'
                                ELSE 'Reconciled'
                            END,
           AsOfSyncRunId  = l.SyncRunId,
           AsOfUtc        = l.StartedAtUtc
      FROM latest l
      LEFT JOIN hub h        ON h.Source = l.Source AND h.Entity = l.Entity
      LEFT JOIN exceptions x ON x.Source = l.Source AND x.Entity = l.Entity
     WHERE l.rn = 1
     ORDER BY l.Source, l.Entity;
END;
GO

/* ===========================================================================
   Health snapshot for monitoring. The worker reads this every few minutes and
   publishes it as Prometheus gauges, so support can alert on state that only
   lives in SQL: the exception queue and runs that never finished.
   Read-only and cheap: DeadLetter is filtered on open statuses, SyncRun on a
   short time window via IX_SyncRun_Started.
=========================================================================== */
CREATE OR ALTER PROCEDURE ops.usp_GetHealthSnapshot
    @StuckAfterMinutes int = 30
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Open dead letters, by bounded labels only (no keys)
    SELECT dl.Source, dl.Entity, dl.ErrorType,
           OpenCount        = COUNT(*),
           OldestAgeSeconds = DATEDIFF(SECOND, MIN(dl.CreatedAtUtc), SYSUTCDATETIME())
      FROM ops.DeadLetter dl
     WHERE dl.Status IN ('New', 'ReplayRequested')
     GROUP BY dl.Source, dl.Entity, dl.ErrorType;

    -- 2. Runs still 'Running' long after they started: the worker crashed or was killed mid-run
    SELECT r.Source, r.RunType, StuckRuns = COUNT(*)
      FROM ops.SyncRun r
     WHERE r.Status = 'Running'
       AND r.StartedAtUtc < DATEADD(MINUTE, -@StuckAfterMinutes, SYSUTCDATETIME())
     GROUP BY r.Source, r.RunType;
END;
GO

/* Housekeeping: staging is for audit and replay, not forever */
CREATE OR ALTER PROCEDURE ops.usp_PurgeStaging
    @KeepDays int = 7
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Cutoff datetime2(3) = DATEADD(DAY, -@KeepDays, SYSUTCDATETIME());
    -- Delete in chunks to keep the transaction log and locking under control
    WHILE 1 = 1
    BEGIN
        DELETE TOP (5000) FROM stg.Matter WHERE StagedAtUtc < @Cutoff;
        IF @@ROWCOUNT = 0 BREAK;
    END;
    WHILE 1 = 1
    BEGIN
        DELETE TOP (5000) FROM stg.Client WHERE StagedAtUtc < @Cutoff;
        IF @@ROWCOUNT = 0 BREAK;
    END;
END;
GO
