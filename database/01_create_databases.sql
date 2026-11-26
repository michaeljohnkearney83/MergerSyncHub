/*
    01_create_databases.sql
    Creates the two databases used by the demo:
      FirmA_Legacy  - stands in for the legacy UK firm's practice management database
      SyncHub       - the integration hub (staging, core, ops and reporting schemas)

    In real life these would usually be on different servers. The worker never
    queries across databases, so you can move FirmA_Legacy elsewhere by changing
    a connection string.
*/
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
IF DB_ID(N'FirmA_Legacy') IS NULL
    CREATE DATABASE FirmA_Legacy;
GO
IF DB_ID(N'SyncHub') IS NULL
    CREATE DATABASE SyncHub;
GO
-- SIMPLE recovery: the transaction log is reused after each checkpoint, so it stays small.
-- Under FULL (SQL Server's default from the model database) the log keeps every change until a
-- log backup is taken; nothing here takes one, and with the worker writing every minute the log
-- grew to twice the size of the data. Production would stay on FULL for point-in-time restore and
-- schedule log backups (e.g. every 15 minutes with SQL Server Agent) instead.
ALTER DATABASE FirmA_Legacy SET RECOVERY SIMPLE;
ALTER DATABASE SyncHub SET RECOVERY SIMPLE;
GO
