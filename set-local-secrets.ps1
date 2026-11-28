<#
    Stores this machine's SQL connection strings in .NET user secrets, so no password
    is kept in the project (or in Git). Secrets live in your Windows profile under
    %APPDATA%\Microsoft\UserSecrets\<id>\secrets.json and are only loaded when an app
    runs in the Development environment (set by each project's launchSettings.json).

    Docker SQL Server from docker-compose.yml:
        .\set-local-secrets.ps1 -Password '<your sa password>'

    Another server:
        .\set-local-secrets.ps1 -Server "localhost\SQLEXPRESS" -User myuser -Password 'secret'

    Check what's stored:
        dotnet user-secrets list --project src/Services/SyncHub.Worker
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'Password',
    Justification = 'The local Docker sa password goes into a plain-text connection string in user secrets; a SecureString would be converted straight back.')]
param(
    [string]$Server = "localhost,14333",
    [string]$User = "sa",
    [Parameter(Mandatory = $true)][string]$Password
)

$ErrorActionPreference = "Stop"

# The part every connection string shares; Conn adds the database and an Application Name (which shows in
# sys.dm_exec_sessions and Query Store, so you can tell the worker's queries from the API's).
$serverAndLogin = "Server=$Server;User Id=$User;Password=$Password;TrustServerCertificate=True"

function Conn([string]$database, [string]$app, [string]$extra = "") {
    "$serverAndLogin;Database=$database;Application Name=$app$extra"
}

$secrets = @(
    @{ Project = "src/Services/SyncHub.Worker";     Key = "ConnectionStrings:SyncHub";   Value = (Conn "SyncHub" "SyncHub.Worker") },
    @{ Project = "src/Services/SyncHub.Worker";     Key = "ConnectionStrings:FirmA";     Value = (Conn "FirmA_Legacy" "SyncHub.Worker" ";ApplicationIntent=ReadOnly") },
    @{ Project = "src/Services/SyncHub.SupportApi"; Key = "ConnectionStrings:SyncHub";   Value = (Conn "SyncHub" "SyncHub.SupportApi") },
    @{ Project = "src/Mocks/CorpDirectory.Api";  Key = "ConnectionStrings:Directory"; Value = (Conn "CorpDirectory" "CorpDirectory.Api") }
)

foreach ($s in $secrets) {
    dotnet user-secrets set $s.Key $s.Value --project (Join-Path $PSScriptRoot $s.Project) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to set $($s.Key) for $($s.Project)" }
    Write-Host "Set $($s.Key) for $($s.Project)" -ForegroundColor Green
}

Write-Host "Done. Restart the .NET apps to pick up the secrets." -ForegroundColor Cyan
