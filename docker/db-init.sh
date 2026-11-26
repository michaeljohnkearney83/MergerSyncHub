#!/bin/bash
# One-off container: creates the databases the first time the stack starts.
# setup.ps1 drops and recreates everything, so this only runs the scripts when the databases are
# missing; otherwise every `docker compose up` would wipe the synced data and directory users.
set -euo pipefail

SQLCMD=/opt/mssql-tools18/bin/sqlcmd
run() { "$SQLCMD" -S sql -U sa -P "$MSSQL_SA_PASSWORD" -C -b "$@"; }

existing=$(run -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name IN ('FirmA_Legacy', 'SyncHub', 'CorpDirectory')" | tr -d '[:space:]')
if [ "$existing" = "3" ]; then
    echo "db-init: all three databases exist; skipping the setup scripts."
else
    echo "db-init: $existing of 3 databases found; running the setup scripts."
    for script in 01_create_databases.sql 02_firmA_legacy.sql 03_synchub_schema.sql 04_synchub_procedures.sql 05_reporting.sql 06_directory.sql; do
        echo "db-init: $script"
        run -I -i "/scripts/$script"     # -I: QUOTED_IDENTIFIER ON (filtered indexes need it)
    done
fi

# The reporting login is safe to re-apply on every start (it only creates or resets the login),
# so a password changed in .env takes effect on the next `docker compose up`.
if [ -n "${REPORTING_SQL_PASSWORD:-}" ]; then
    echo "db-init: 07_reporting_login.sql"
    run -I -i /scripts/07_reporting_login.sql -v ReportingPassword="$REPORTING_SQL_PASSWORD"
else
    echo "db-init: REPORTING_SQL_PASSWORD not set; skipping the reporting login."
fi
echo "db-init: done."
