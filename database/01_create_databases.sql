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
