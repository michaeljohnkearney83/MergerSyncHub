/*
    07_reporting_login.sql
    A least-privilege login for Power BI and SSRS. Reporting tools get read access to the
    rpt (reporting) schema and can run the two SSRS procedures, and nothing else: no core
    tables, no writes, no rpt.usp_LoadReporting.

    The password is never stored in this repo. It is passed in as a sqlcmd variable:
        sqlcmd -S "localhost,14333" -U sa -P '<sa password>' -C -I -i 07_reporting_login.sql -v ReportingPassword="<password>"
    setup.ps1 and the Docker db-init container pass it from REPORTING_SQL_PASSWORD (see .env.example).

    Re-runnable: creates the login and user if missing, otherwise resets the password.
    The SSRS procedures read core and ops tables; that works without granting those tables,
    through ownership chaining (procedure and tables are both owned by dbo).
*/
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
USE master;
GO
IF SUSER_ID(N'synchub_reporting') IS NULL
    CREATE LOGIN synchub_reporting WITH PASSWORD = N'$(ReportingPassword)', CHECK_POLICY = ON, DEFAULT_DATABASE = SyncHub;
ELSE
    ALTER LOGIN synchub_reporting WITH PASSWORD = N'$(ReportingPassword)';
GO
USE SyncHub;
GO
IF USER_ID(N'synchub_reporting') IS NULL
    CREATE USER synchub_reporting FOR LOGIN synchub_reporting;
GO
GRANT SELECT ON SCHEMA::rpt TO synchub_reporting;
GRANT EXECUTE ON OBJECT::rpt.usp_SsrsExceptions TO synchub_reporting;
GRANT EXECUTE ON OBJECT::rpt.usp_SsrsSyncRunSummary TO synchub_reporting;
GO
