/*
    03_synchub_schema.sql
    Integration hub schemas:
      stg  - raw-ish staging rows, one batch per sync run (kept for audit)
      core - the combined client and matter register
      ops  - sync runs, watermarks, dead letters
      rpt  - reporting star schema (created in 05_reporting.sql)

    Re-runnable: drops and recreates everything in SyncHub.
*/
USE SyncHub;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF SCHEMA_ID(N'stg')  IS NULL EXEC (N'CREATE SCHEMA stg');
IF SCHEMA_ID(N'core') IS NULL EXEC (N'CREATE SCHEMA core');
IF SCHEMA_ID(N'ops')  IS NULL EXEC (N'CREATE SCHEMA ops');
IF SCHEMA_ID(N'rpt')  IS NULL EXEC (N'CREATE SCHEMA rpt');
GO

/* Drop in dependency order so the script can be re-run */
DROP PROCEDURE IF EXISTS core.usp_SaveMatchCandidates;
IF TYPE_ID(N'core.MatchCandidateList') IS NOT NULL DROP TYPE core.MatchCandidateList;
DROP TABLE IF EXISTS core.ClientMatchCandidate;
DROP TABLE IF EXISTS core.Matter;
DROP TABLE IF EXISTS core.Client;
DROP TABLE IF EXISTS core.PracticeAreaMap;
DROP TABLE IF EXISTS stg.Matter;
DROP TABLE IF EXISTS stg.Client;
DROP TABLE IF EXISTS ops.DeadLetter;
DROP TABLE IF EXISTS ops.SyncRunEntity;
DROP TABLE IF EXISTS ops.SyncRun;
DROP TABLE IF EXISTS ops.Watermark;
GO

/* ===========================================================================
   ops: run tracking, watermarks, dead letters
=========================================================================== */
CREATE TABLE ops.SyncRun
(
    SyncRunId       bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SyncRun PRIMARY KEY,
    Source          varchar(20)      NOT NULL,
    RunType         varchar(20)      NOT NULL CONSTRAINT CK_SyncRun_RunType CHECK (RunType IN ('Scheduled','Replay')),
    CorrelationId   uniqueidentifier NOT NULL,   -- also written to every log line for this run
    StartedAtUtc    datetime2(3)     NOT NULL CONSTRAINT DF_SyncRun_Started DEFAULT (SYSUTCDATETIME()),
    FinishedAtUtc   datetime2(3)     NULL,
    Status          varchar(30)      NOT NULL CONSTRAINT DF_SyncRun_Status DEFAULT ('Running')
                    CONSTRAINT CK_SyncRun_Status CHECK (Status IN ('Running','Succeeded','SucceededWithErrors','Failed')),
    ErrorMessage    nvarchar(4000)   NULL
);
CREATE INDEX IX_SyncRun_Started ON ops.SyncRun (StartedAtUtc DESC);

CREATE TABLE ops.SyncRunEntity
(
    SyncRunId       bigint      NOT NULL CONSTRAINT FK_SyncRunEntity_Run REFERENCES ops.SyncRun (SyncRunId),
    Entity          varchar(20) NOT NULL,
    SourceTotal     int         NULL,   -- total rows the source says it holds (for reconciliation)
    RecordsRead     int         NOT NULL DEFAULT 0,
    RecordsStaged   int         NOT NULL DEFAULT 0,
    RecordsInserted int         NOT NULL DEFAULT 0,
    RecordsUpdated  int         NOT NULL DEFAULT 0,
    RecordsFailed   int         NOT NULL DEFAULT 0,
    CONSTRAINT PK_SyncRunEntity PRIMARY KEY (SyncRunId, Entity)
);

CREATE TABLE ops.Watermark
(
    Source          varchar(20)  NOT NULL,
    Entity          varchar(20)  NOT NULL,
    LastModified    datetime2(3) NOT NULL,   -- in the source system's own time basis
    UpdatedAtUtc    datetime2(3) NOT NULL CONSTRAINT DF_Watermark_Updated DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Watermark PRIMARY KEY (Source, Entity)
);

CREATE TABLE ops.DeadLetter
(
    DeadLetterId        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DeadLetter PRIMARY KEY,
    FirstSyncRunId      bigint         NOT NULL,
    LastSyncRunId       bigint         NOT NULL,
    Source              varchar(20)    NOT NULL,
    Entity              varchar(20)    NOT NULL,
    SourceKey           varchar(50)    NOT NULL,
    RelatedClientKey    varchar(50)    NULL,      -- for orphan matters: the client they are waiting for
    ErrorType           varchar(50)    NOT NULL,
    ErrorMessage        nvarchar(2000) NOT NULL,
    Payload             nvarchar(max)  NOT NULL,   -- the raw source record, so it can be fixed and replayed
    Status              varchar(20)    NOT NULL CONSTRAINT DF_DeadLetter_Status DEFAULT ('New')
                        CONSTRAINT CK_DeadLetter_Status CHECK (Status IN ('New','ReplayRequested','Resolved','Ignored')),
    OccurrenceCount     int            NOT NULL CONSTRAINT DF_DeadLetter_Occ DEFAULT (1),
    ReplayAttempts      int            NOT NULL CONSTRAINT DF_DeadLetter_Replay DEFAULT (0),
    CreatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_DeadLetter_Created DEFAULT (SYSUTCDATETIME()),
    LastSeenAtUtc       datetime2(3)   NOT NULL CONSTRAINT DF_DeadLetter_LastSeen DEFAULT (SYSUTCDATETIME()),
    PayloadEditedBy     nvarchar(100)  NULL,
    PayloadEditedAtUtc  datetime2(3)   NULL,
    ResolvedAtUtc       datetime2(3)   NULL,
    ResolutionNote      nvarchar(1000) NULL
);
-- At most one open dead letter per source record: repeats bump OccurrenceCount instead of piling up
CREATE UNIQUE INDEX UX_DeadLetter_Open
    ON ops.DeadLetter (Source, Entity, SourceKey)
    WHERE Status IN ('New', 'ReplayRequested');
CREATE INDEX IX_DeadLetter_Status ON ops.DeadLetter (Status, LastSeenAtUtc DESC);
GO

/* ===========================================================================
   stg: one batch per run. RawPayload is kept so failures can be replayed.
=========================================================================== */
CREATE TABLE stg.Client
(
    StagedClientId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_stgClient PRIMARY KEY,
    SyncRunId           bigint         NOT NULL,
    Source              varchar(20)    NOT NULL,
    SourceClientKey     varchar(50)    NOT NULL,
    ClientName          nvarchar(200)  NOT NULL,
    NormalisedName      nvarchar(200)  NOT NULL,
    Country             nvarchar(60)   NULL,
    PostCode            nvarchar(12)   NULL,
    OpenedOn            date           NULL,
    IsActive            bit            NOT NULL,
    SourceModifiedUtc   datetime2(3)   NOT NULL,
    RawPayload          nvarchar(max)  NOT NULL,
    StagedAtUtc         datetime2(3)   NOT NULL CONSTRAINT DF_stgClient_Staged DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_stgClient_Run ON stg.Client (SyncRunId, Source, SourceClientKey);

CREATE TABLE stg.Matter
(
    StagedMatterId      bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_stgMatter PRIMARY KEY,
    SyncRunId           bigint         NOT NULL,
    Source              varchar(20)    NOT NULL,
    SourceMatterKey     varchar(50)    NOT NULL,
    SourceClientKey     varchar(50)    NOT NULL,
    MatterNumber        nvarchar(30)   NOT NULL,
    Description         nvarchar(500)  NULL,
    PracticeAreaCode    varchar(30)    NULL,
    FeeEarner           nvarchar(50)   NULL,
    OpenedOn            date           NULL,
    ClosedOn            date           NULL,
    IsOpen              bit            NOT NULL,
    SourceModifiedUtc   datetime2(3)   NOT NULL,
    RawPayload          nvarchar(max)  NOT NULL,
    StagedAtUtc         datetime2(3)   NOT NULL CONSTRAINT DF_stgMatter_Staged DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_stgMatter_Run ON stg.Matter (SyncRunId, Source, SourceMatterKey);
GO

/* ===========================================================================
   core: the combined register
=========================================================================== */
CREATE TABLE core.Client
(
    ClientId            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Client PRIMARY KEY,
    Source              varchar(20)    NOT NULL,
    SourceClientKey     varchar(50)    NOT NULL,
    ClientName          nvarchar(200)  NOT NULL,
    NormalisedName      nvarchar(200)  NOT NULL,
    Country             nvarchar(60)   NULL,
    PostCode            nvarchar(12)   NULL,
    OpenedOn            date           NULL,
    IsActive            bit            NOT NULL,
    SourceModifiedUtc   datetime2(3)   NOT NULL,
    RowHash             binary(32)     NOT NULL,   -- skip no-op updates
    CreatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_Client_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_Client_Updated DEFAULT (SYSUTCDATETIME()),
    LastSyncRunId       bigint         NOT NULL,
    CONSTRAINT UX_Client_SourceKey UNIQUE (Source, SourceClientKey)
);
CREATE INDEX IX_Client_NormalisedName ON core.Client (NormalisedName) INCLUDE (PostCode, Source);

CREATE TABLE core.Matter
(
    MatterId            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Matter PRIMARY KEY,
    Source              varchar(20)    NOT NULL,
    SourceMatterKey     varchar(50)    NOT NULL,
    ClientId            int            NOT NULL CONSTRAINT FK_Matter_Client REFERENCES core.Client (ClientId),
    MatterNumber        nvarchar(30)   NOT NULL,
    Description         nvarchar(500)  NULL,
    PracticeAreaCode    varchar(30)    NULL,      -- kept raw; mapped at reporting time
    FeeEarner           nvarchar(50)   NULL,
    OpenedOn            date           NULL,
    ClosedOn            date           NULL,
    IsOpen              bit            NOT NULL,
    SourceModifiedUtc   datetime2(3)   NOT NULL,
    RowHash             binary(32)     NOT NULL,
    CreatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_Matter_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_Matter_Updated DEFAULT (SYSUTCDATETIME()),
    LastSyncRunId       bigint         NOT NULL,
    CONSTRAINT UX_Matter_SourceKey UNIQUE (Source, SourceMatterKey)
);
CREATE INDEX IX_Matter_Client ON core.Matter (ClientId) INCLUDE (IsOpen);

/* Standardises each firm's department codes onto the combined firm's practice areas.
   Unmapped codes show up in reporting as 'Unmapped' until a row is added here. */
CREATE TABLE core.PracticeAreaMap
(
    Source              varchar(20)    NOT NULL,
    SourceCode          varchar(30)    NOT NULL,
    PracticeArea        nvarchar(100)  NOT NULL,
    CONSTRAINT PK_PracticeAreaMap PRIMARY KEY (Source, SourceCode)
);
INSERT core.PracticeAreaMap (Source, SourceCode, PracticeArea) VALUES
 ('FirmA','LIT','Major Litigation'),
 ('FirmA','CORP','Critical Transactions'),
 ('FirmA','IP','Strategic Intellectual Property'),
 ('FirmA','PCS','Private Wealth'),
 ('FirmA','RE','Real Estate'),
 ('FirmA','EMP','Employment'),
 ('FirmA','TAX','Tax'),
 ('FirmB','LITIGATION','Major Litigation'),
 ('FirmB','M&A','Critical Transactions'),
 ('FirmB','CAPITAL_MARKETS','Critical Transactions'),
 ('FirmB','IP','Strategic Intellectual Property'),
 ('FirmB','PRIVATE_WEALTH','Private Wealth'),
 ('FirmB','REAL_ESTATE','Real Estate'),
 ('FirmB','TAX','Tax');
-- Deliberately missing: FirmA 'MISC' and FirmB 'LABOR_EMPLOYMENT'

/* Possible duplicate clients, for a person to review (similar to a conflicts check) */
CREATE TABLE core.ClientMatchCandidate
(
    CandidateId         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ClientMatchCandidate PRIMARY KEY,
    ClientIdA           int            NOT NULL CONSTRAINT FK_Candidate_A REFERENCES core.Client (ClientId),
    ClientIdB           int            NOT NULL CONSTRAINT FK_Candidate_B REFERENCES core.Client (ClientId),
    Score               decimal(5,4)   NOT NULL,
    MatchReason         nvarchar(200)  NOT NULL,
    Status              varchar(20)    NOT NULL CONSTRAINT DF_Candidate_Status DEFAULT ('Pending')
                        CONSTRAINT CK_Candidate_Status CHECK (Status IN ('Pending','ConfirmedDuplicate','NotDuplicate')),
    ReviewedBy          nvarchar(100)  NULL,
    ReviewedAtUtc       datetime2(3)   NULL,
    CreatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_Candidate_Created DEFAULT (SYSUTCDATETIME()),
    LastScoredAtUtc     datetime2(3)   NOT NULL CONSTRAINT DF_Candidate_Scored DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UX_Candidate_Pair UNIQUE (ClientIdA, ClientIdB),
    CONSTRAINT CK_Candidate_Order CHECK (ClientIdA < ClientIdB)   -- each pair stored once
);
GO

CREATE TYPE core.MatchCandidateList AS TABLE
(
    ClientIdA   int           NOT NULL,
    ClientIdB   int           NOT NULL,
    Score       decimal(5,4)  NOT NULL,
    MatchReason nvarchar(200) NOT NULL,
    PRIMARY KEY (ClientIdA, ClientIdB)
);
GO
