<#
.SYNOPSIS
    Sets up Microsoft Entra ID for Merger Sync Hub: the real-world replacement for the mock corporate directory.

.DESCRIPTION
    Creates (or updates, if they already exist) in the signed-in tenant:
      1. App registration "SyncHub Support API": exposes an API scope and four app roles
         (SyncHub.Viewer, SyncHub.Support, SyncHub.DataSteward, SyncHub.Admin). Its enterprise application
         requires assignment, so only people given a role can get a token (like jordan.lee in the demo).
      2. App registration "SyncHub Support Dashboard": a single-page app (MSAL) allowed to call that API.
      3. Four security groups, one per role.
      4. Role assignments: groups to roles with -AssignGroups (needs Entra ID P1 or higher), otherwise the
         signed-in user is made SyncHub.Admin so you can sign in on the free tier.

    Everything here is tenant-level Entra configuration: it costs nothing and doesn't need an Azure subscription.
    Re-runnable: objects are found by display name and updated rather than duplicated.
    -DryRun looks everything up but changes nothing, printing what it would do.

    Needs: Azure CLI signed in (az login --allow-no-subscriptions works) as someone who can create app
    registrations and groups and grant admin consent (e.g. Application Administrator + Groups Administrator,
    or Global Administrator in a personal tenant).

.EXAMPLE
    .\infra\entra-setup.ps1 -DryRun
    .\infra\entra-setup.ps1
    .\infra\entra-setup.ps1 -SupportWebUrl https://synchub-support-web.<domain> -AssignGroups
#>
[CmdletBinding()]
param(
    [string]$Prefix = "SyncHub",
    # Where the dashboard runs: always localhost for development, plus the Azure URL once deployed.
    [string[]]$SupportWebUrl = @("http://localhost:5173"),
    # Assign the groups to the app roles. Group assignment to app roles needs Entra ID P1 or higher.
    [switch]$AssignGroups,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$graph = "https://graph.microsoft.com/v1.0"

# Fixed ids keep app roles and the scope stable across runs (Entra rejects changing a role's id while it's in use).
$roles = @(
    @{ Value = "$Prefix.Viewer";      Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e01"; Group = "$Prefix Viewers";          Name = "Viewer";       Description = "Read the support dashboard" }
    @{ Value = "$Prefix.Support";     Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e02"; Group = "$Prefix Support Analysts"; Name = "Support Analyst"; Description = "Fix, replay and ignore integration exceptions" }
    @{ Value = "$Prefix.DataSteward"; Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e03"; Group = "$Prefix Data Stewards";    Name = "Data Steward"; Description = "Decide duplicate clients and maintain practice-area mappings" }
    @{ Value = "$Prefix.Admin";       Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e04"; Group = "$Prefix Admins";           Name = "Administrator"; Description = "Everything in the support dashboard" }
)
$scopeId = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e10"

function Invoke-Graph {
    <# Calls Microsoft Graph through az rest. Writes are skipped (and described) under -DryRun. #>
    param([string]$Method, [string]$Uri, $Body)
    $isWrite = $Method -ne "GET"
    if ($isWrite -and $DryRun) {
        Write-Host "  [dry run] would $Method $Uri" -ForegroundColor DarkYellow
        return $null
    }
    $azArgs = @("rest", "--method", $Method, "--uri", $Uri, "--headers", "Content-Type=application/json")
    if ($null -ne $Body) {
        # Pass the body through a file: inline JSON quoting differs between shells.
        $file = New-TemporaryFile
        # UTF-8 without a byte-order mark: az rest can't parse a BOM (Windows PowerShell's utf8 adds one).
        [System.IO.File]::WriteAllText($file, ($Body | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
        $azArgs += @("--body", "@$file")
    }
    try {
        $out = az @azArgs
        if ($LASTEXITCODE -ne 0) { throw "az rest $Method $Uri failed" }
        if ($out) { return (($out -join "`n") | ConvertFrom-Json) }
    } finally {
        if ($file) { Remove-Item $file -ErrorAction SilentlyContinue }
    }
}

function Find-One {
    <# The first object in a Graph collection whose displayName matches, or $null. #>
    param([string]$Collection, [string]$DisplayName)
    $filter = [uri]::EscapeDataString("displayName eq '$DisplayName'")
    $result = Invoke-Graph GET "$graph/$Collection`?`$filter=$filter"
    if ($result.value.Count -gt 0) { return $result.value[0] } else { return $null }
}

function Get-OrCreateApp {
    <# Finds an app registration by name, or creates it. Returns the application object (or a placeholder in a dry run). #>
    param([string]$DisplayName)
    $app = Find-One "applications" $DisplayName
    if ($app) { Write-Host "  found app registration '$DisplayName'"; return $app }
    Write-Host "  creating app registration '$DisplayName'"
    $created = Invoke-Graph POST "$graph/applications" @{ displayName = $DisplayName; signInAudience = "AzureADMyOrg" }
    if ($created) { return $created }
    return [pscustomobject]@{ id = "<new-$DisplayName>"; appId = "<new-$DisplayName-appId>" }
}

function Get-OrCreateServicePrincipal {
    <# The enterprise application (service principal) for an app registration. #>
    param($App)
    if ($App.appId -like "<new-*") { return [pscustomobject]@{ id = "<new-sp-$($App.appId)>" } }
    $filter = [uri]::EscapeDataString("appId eq '$($App.appId)'")
    $sp = (Invoke-Graph GET "$graph/servicePrincipals?`$filter=$filter").value | Select-Object -First 1
    if ($sp) { return $sp }
    Write-Host "  creating enterprise application for $($App.appId)"
    $created = Invoke-Graph POST "$graph/servicePrincipals" @{ appId = $App.appId }
    if ($created) { return $created } else { return [pscustomobject]@{ id = "<new-sp-$($App.appId)>" } }
}

# ---------------------------------------------------------------------------------------------- start
# Windows PowerShell treats a native command's error output as fatal under "Stop", so check softly here.
$ErrorActionPreference = "Continue"
$accountJson = az account show --query "{tenantId: tenantId}" -o json 2>$null
$ErrorActionPreference = "Stop"
if ($LASTEXITCODE -ne 0 -or -not $accountJson) {
    throw "The Azure CLI has no signed-in tenant. Sign in at tenant level (no subscription needed): az login --allow-no-subscriptions"
}
$account = ($accountJson -join "`n") | ConvertFrom-Json
$tenantId = $account.tenantId
$me = Invoke-Graph GET "$graph/me?`$select=id,userPrincipalName"
Write-Host "Tenant $tenantId, signed in as $($me.userPrincipalName)$(if ($DryRun) { ' (dry run: no changes)' })" -ForegroundColor Cyan

# ---------------------------------------------------------------------------------------------- 1. API
Write-Host "1. Support API app registration" -ForegroundColor Cyan
$api = Get-OrCreateApp "$Prefix Support API"
$apiUri = "api://$($api.appId)"   # api://<appId> always passes the tenant's identifier-URI policy
Invoke-Graph PATCH "$graph/applications/$($api.id)" @{
    identifierUris = @($apiUri)
    api = @{
        requestedAccessTokenVersion = 2   # v2 tokens: issuer https://login.microsoftonline.com/<tenant>/v2.0
        oauth2PermissionScopes = @(@{
            id = $scopeId; value = "access_as_user"; type = "User"; isEnabled = $true
            adminConsentDisplayName = "Use the SyncHub Support API"
            adminConsentDescription = "Lets the support dashboard call the SyncHub Support API as the signed-in user."
            userConsentDisplayName  = "Use the SyncHub Support API"
            userConsentDescription  = "Lets the support dashboard call the SyncHub Support API for you."
        })
    }
    appRoles = @($roles | ForEach-Object {
        @{ id = $_.Id; value = $_.Value; displayName = $_.Name; description = $_.Description
           allowedMemberTypes = @("User"); isEnabled = $true }
    })
} | Out-Null
$apiSp = Get-OrCreateServicePrincipal $api
# Only users or groups assigned a role can get a token for the API: no role, no access.
Invoke-Graph PATCH "$graph/servicePrincipals/$($apiSp.id)" @{ appRoleAssignmentRequired = $true } | Out-Null

# ---------------------------------------------------------------------------------------------- 2. dashboard
Write-Host "2. Support Dashboard app registration (single-page app)" -ForegroundColor Cyan
$spa = Get-OrCreateApp "$Prefix Support Dashboard"
Invoke-Graph PATCH "$graph/applications/$($spa.id)" @{
    spa = @{ redirectUris = @($SupportWebUrl) }
    requiredResourceAccess = @(@{
        resourceAppId = $api.appId
        resourceAccess = @(@{ id = $scopeId; type = "Scope" })
    })
} | Out-Null
$spaSp = Get-OrCreateServicePrincipal $spa

# Admin consent for the dashboard to call the API on users' behalf (so users aren't prompted).
$existingGrant = if ($spaSp.id -like "<new-*") { $null } else {
    (Invoke-Graph GET "$graph/oauth2PermissionGrants?`$filter=$([uri]::EscapeDataString("clientId eq '$($spaSp.id)'"))").value |
        Where-Object { $_.resourceId -eq $apiSp.id } | Select-Object -First 1 }
if (-not $existingGrant) {
    Write-Host "  granting admin consent: dashboard -> API (access_as_user)"
    Invoke-Graph POST "$graph/oauth2PermissionGrants" @{
        clientId = $spaSp.id; consentType = "AllPrincipals"; resourceId = $apiSp.id; scope = "access_as_user"
    } | Out-Null
} else { Write-Host "  admin consent already granted" }

# ---------------------------------------------------------------------------------------------- 3. groups
Write-Host "3. Security groups" -ForegroundColor Cyan
foreach ($role in $roles) {
    $group = Find-One "groups" $role.Group
    if (-not $group) {
        Write-Host "  creating group '$($role.Group)'"
        $group = Invoke-Graph POST "$graph/groups" @{
            displayName = $role.Group; description = "Grants $($role.Value) in the SyncHub Support dashboard"
            mailEnabled = $false; mailNickname = ($role.Group -replace '\s', '').ToLower(); securityEnabled = $true
        }
    } else { Write-Host "  found group '$($role.Group)'" }
    $role.GroupId = if ($group) { $group.id } else { "<new-group>" }
}

# ---------------------------------------------------------------------------------------------- 4. assignments
Write-Host "4. Role assignments" -ForegroundColor Cyan
$assigned = if ($apiSp.id -like "<new-*") { @() } else {
    (Invoke-Graph GET "$graph/servicePrincipals/$($apiSp.id)/appRoleAssignedTo").value }

function Grant-AppRole {
    param([string]$PrincipalId, [string]$Label, $Role)
    if ($assigned | Where-Object { $_.principalId -eq $PrincipalId -and $_.appRoleId -eq $Role.Id }) {
        Write-Host "  $Label already has $($Role.Value)"; return
    }
    Write-Host "  assigning $($Role.Value) to $Label"
    Invoke-Graph POST "$graph/servicePrincipals/$($apiSp.id)/appRoleAssignedTo" @{
        principalId = $PrincipalId; resourceId = $apiSp.id; appRoleId = $Role.Id
    } | Out-Null
}

if ($AssignGroups) {
    foreach ($role in $roles) { Grant-AppRole $role.GroupId "group '$($role.Group)'" $role }
} else {
    Write-Host "  (groups not assigned: assigning groups to app roles needs Entra ID P1; rerun with -AssignGroups if you have it)" -ForegroundColor DarkGray
    Grant-AppRole $me.id "you ($($me.userPrincipalName))" ($roles | Where-Object Value -eq "$Prefix.Admin")
}

# ---------------------------------------------------------------------------------------------- result
Write-Host ""
Write-Host "Settings for the apps (not secrets; keep them out of Git anyway, e.g. user secrets or Container App env vars):" -ForegroundColor Green
Write-Host "  Support API  Auth:Authority = https://login.microsoftonline.com/$tenantId/v2.0"
Write-Host "  Support API  Auth:Audience  = $($api.appId)"
Write-Host "  Dashboard    clientId       = $($spa.appId)"
Write-Host "  Dashboard    scope          = $apiUri/access_as_user"
