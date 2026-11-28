<#
    Runs the database scripts in order using sqlcmd.

    Local SQL Server, Windows authentication:
        .\setup.ps1
        .\setup.ps1 -Server "localhost\SQLEXPRESS"

    Docker (see docker-compose.yml):
        .\setup.ps1 -Server "localhost,14333" -User sa -Password '<your sa password>'
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'Password',
    Justification = 'Local Docker sa password, passed straight to sqlcmd -P, which only takes plain text.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'ReportingPassword',
    Justification = 'Passed to sqlcmd as a -v variable (plain text) and defaults from an environment variable.')]
param(
    [string]$Server = "localhost",
    [string]$User,
    [string]$Password,
    # Password for the read-only reporting login (Power BI, SSRS). Defaults to REPORTING_SQL_PASSWORD;
    # if neither is set, 07_reporting_login.sql is skipped.
    [string]$ReportingPassword = $env:REPORTING_SQL_PASSWORD
)

$ErrorActionPreference = "Stop"
$scripts = @(
    "01_create_databases.sql",
    "02_firmA_legacy.sql",
    "03_synchub_schema.sql",
    "04_synchub_procedures.sql",
    "05_reporting.sql",
    "06_directory.sql"
)

foreach ($script in $scripts) {
    $path = Join-Path $PSScriptRoot $script
    Write-Host "Running $script..." -ForegroundColor Cyan
    # -b: stop on error, -C: trust server certificate (local dev only),
    # -I: QUOTED_IDENTIFIER ON (sqlcmd defaults it OFF; filtered indexes need it ON)
    if ($User) {
        sqlcmd -S $Server -U $User -P $Password -C -b -I -i $path
    } else {
        sqlcmd -S $Server -E -C -b -I -i $path
    }
    if ($LASTEXITCODE -ne 0) { throw "$script failed" }
}

if ($ReportingPassword) {
    Write-Host "Running 07_reporting_login.sql..." -ForegroundColor Cyan
    $path = Join-Path $PSScriptRoot "07_reporting_login.sql"
    $var = "ReportingPassword=$ReportingPassword"
    if ($User) { sqlcmd -S $Server -U $User -P $Password -C -b -I -i $path -v $var }
    else       { sqlcmd -S $Server -E -C -b -I -i $path -v $var }
    if ($LASTEXITCODE -ne 0) { throw "07_reporting_login.sql failed" }
} else {
    Write-Host "Skipping 07_reporting_login.sql: pass -ReportingPassword or set REPORTING_SQL_PASSWORD." -ForegroundColor Yellow
}

Write-Host "Database setup complete." -ForegroundColor Green
