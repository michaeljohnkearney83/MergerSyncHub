/*
    Performance demo: the worker's incremental extract from the legacy database.

    The worker asks Firm A for "everything changed since my watermark":

        SELECT ... FROM dbo.MATTER WHERE LAST_MODIFIED >= @Since ORDER BY LAST_MODIFIED, MATTER_ID

    Firm A has no index on LAST_MODIFIED, so every incremental run scans all ~75k
    rows even when only a handful changed. Fine at 75k; painful at 7.5 million,
    and it competes with fee earners using the live system.

    How to run this demo (SSMS):
      1. Turn on "Include Actual Execution Plan" (Ctrl+M)
      2. Run step 1 and note the Clustered Index Scan and logical reads in Messages
      3. Run step 2 to add the index
      4. Run step 1 again: now an Index Seek with a fraction of the reads
*/
USE FirmA_Legacy;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
SET STATISTICS IO, TIME ON;
GO

/* Step 1: the extract query (same shape as FirmAConnector.cs) */
DECLARE @Since datetime = DATEADD(DAY, -7, GETDATE());

SELECT MATTER_ID, CLIENT_ID, MATTER_NO, DESCRIPTION, DEPT_CODE, FEE_EARNER,
       OPEN_DATE, CLOSE_DATE, STATUS, LAST_MODIFIED
  FROM dbo.MATTER
 WHERE LAST_MODIFIED >= @Since
 ORDER BY LAST_MODIFIED, MATTER_ID;
GO

/* Step 2: the fix. A covering index means the query can seek straight to the
   changed rows and return them in order without a sort or key lookups. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_MATTER_LAST_MODIFIED' AND object_id = OBJECT_ID('dbo.MATTER'))
    CREATE INDEX IX_MATTER_LAST_MODIFIED
        ON dbo.MATTER (LAST_MODIFIED, MATTER_ID)
        INCLUDE (CLIENT_ID, MATTER_NO, DESCRIPTION, DEPT_CODE, FEE_EARNER, OPEN_DATE, CLOSE_DATE, STATUS);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CLIENT_LAST_MODIFIED' AND object_id = OBJECT_ID('dbo.CLIENT'))
    CREATE INDEX IX_CLIENT_LAST_MODIFIED
        ON dbo.CLIENT (LAST_MODIFIED, CLIENT_ID)
        INCLUDE (CLIENT_CODE, CLIENT_NAME, POSTCODE, COUNTRY, OPEN_DATE, STATUS);
GO

/*
    Talking points:
    - Trade-off: the index costs a little on every write to MATTER. LAST_MODIFIED
      changes on every update, so the index is maintained on every update too.
      For a read-mostly practice management table that is worth it.
    - If the database belongs to a vendor and you may not add indexes, the options are:
        * SQL Server Change Tracking (lightweight: which keys changed since version N)
        * Change Data Capture (full change history, more overhead)
        * a rowversion column, if the vendor allows schema changes at all
        * reading from a replica / Always On readable secondary, to protect the live system
    - LAST_MODIFIED is datetime (about 3 ms precision) and set by the application. Rows
      committed late with an earlier timestamp could be missed by ">=". The worker therefore
      re-reads an overlap window (Sync:LookbackMinutes) and relies on the idempotent MERGE.
*/
SET STATISTICS IO, TIME OFF;
GO
