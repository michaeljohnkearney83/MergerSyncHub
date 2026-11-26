<#
    Runs the database scripts in order using sqlcmd.

    Local SQL Server, Windows authentication:
        .\setup.ps1
        .\setup.ps1 -Server "localhost\SQLEXPRESS"

    Docker (see docker-compose.yml):
        .\setup.ps1 -Server "localhost,14333" -User sa -Password '<your sa password>'
#>
param(
    [string]$Server = "localhost",
    [string]$User,
    [string]$Password
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

Write-Host "Database setup complete." -ForegroundColor Green
