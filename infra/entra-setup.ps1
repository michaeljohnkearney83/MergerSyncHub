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

    Three modes:
      -DryRun  looks everything up but changes nothing, printing the writes it would send.
      (none)   creates or updates.
      -Verify  changes nothing either, but reads everything back and checks it is as this script sets it up;
               with -AllowAzureCli it also gets a real access token and checks its audience, issuer and roles.
               Exits 1 if any check fails. Entra replicates for a minute or so: run it again if a just-created
               object isn't found yet.

    Needs: Azure CLI signed in (az login --allow-no-subscriptions works) as someone who can create app
    registrations and groups and grant admin consent (e.g. Application Administrator + Groups Administrator,
    or Global Administrator in a personal tenant).

.EXAMPLE
    .\infra\entra-setup.ps1 -DryRun -AllowAzureCli
    .\infra\entra-setup.ps1 -AllowAzureCli
    .\infra\entra-setup.ps1 -Verify -AllowAzureCli
    .\infra\entra-setup.ps1 -SupportWebUrl https://synchub-support-web.<domain> -AssignGroups
#>
[CmdletBinding()]
param(
    [string]$Prefix = "SyncHub",
    # Where the dashboard runs: always localhost for development, plus the Azure URL once deployed.
    [string[]]$SupportWebUrl = @("http://localhost:5173"),
    # Assign the groups to the app roles. Group assignment to app roles needs Entra ID P1 or higher.
    [switch]$AssignGroups,
    # Pre-authorise the Azure CLI to request tokens for the API, so a test token is one command
    # (az account get-access-token) instead of a browser sign-in through the dashboard. Assignment is still
    # required, so it only gives tokens to people who already have a role. Leave it off in production:
    # the parameters describe the desired state, so running without it removes the pre-authorisation.
    [switch]$AllowAzureCli,
    [switch]$DryRun,
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$graph = "https://graph.microsoft.com/v1.0"
$azureCliAppId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"   # Microsoft's well-known, fixed client id for the Azure CLI
# The portal tags the service principals it creates with this, and its Enterprise applications list filters on it by
# default: without it, objects created through Graph only show under "Application type: All applications".
$enterpriseAppTag = "WindowsAzureActiveDirectoryIntegratedApp"

# Fixed ids keep app roles and the scope stable across runs (Entra rejects changing a role's id while it's in use).
$roles = @(
    @{ Value = "$Prefix.Viewer";      Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e01"; Group = "$Prefix Viewers";          Name = "Viewer";       Description = "Read the support dashboard" }
    @{ Value = "$Prefix.Support";     Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e02"; Group = "$Prefix Support Analysts"; Name = "Support Analyst"; Description = "Fix, replay and ignore integration exceptions" }
    @{ Value = "$Prefix.DataSteward"; Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e03"; Group = "$Prefix Data Stewards";    Name = "Data Steward"; Description = "Decide duplicate clients and maintain practice-area mappings" }
    @{ Value = "$Prefix.Admin";       Id = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e04"; Group = "$Prefix Admins";           Name = "Administrator"; Description = "Everything in the support dashboard" }
)
$scopeId = "6a1b0c55-6f0e-4b2e-9a51-0d1f2a7c3e10"
$adminRole = $roles | Where-Object Value -eq "$Prefix.Admin"

function Invoke-Graph {
    <# Calls Microsoft Graph through az rest. Writes are skipped (and described) under -DryRun and -Verify. #>
    param([string]$Method, [string]$Uri, $Body)
    $isWrite = $Method -ne "GET"
    if ($isWrite -and $Verify) { throw "Bug: -Verify must never write ($Method $Uri)" }
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

function Find-ServicePrincipal {
    <# The enterprise application (service principal) for an app id, or $null. #>
    param([string]$AppId)
    $filter = [uri]::EscapeDataString("appId eq '$AppId'")
    return (Invoke-Graph GET "$graph/servicePrincipals?`$filter=$filter").value | Select-Object -First 1
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
    <# The enterprise application for an app registration, created if it doesn't exist yet. #>
    param($App)
    if ($App.appId -like "<new-*") { return [pscustomobject]@{ id = "<new-sp-$($App.appId)>" } }
    $sp = Find-ServicePrincipal $App.appId
    if ($sp) { return $sp }
    Write-Host "  creating enterprise application for $($App.appId)"
    $created = Invoke-Graph POST "$graph/servicePrincipals" @{ appId = $App.appId }
    if ($created) { return $created } else { return [pscustomobject]@{ id = "<new-sp-$($App.appId)>" } }
}

function ConvertFrom-JwtPayload {
    <# Decodes a JWT's claims locally (base64url). No signature check: it only reads what Entra put in the token. #>
    param([string]$Jwt)
    $payload = $Jwt.Split('.')[1].Replace('-', '+').Replace('_', '/')
    switch ($payload.Length % 4) { 2 { $payload += '==' } 3 { $payload += '=' } }
    return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
}

$script:failed = 0
function Assert-Check {
    <# Prints one -Verify check and counts failures. #>
    param([string]$What, [bool]$Ok)
    if ($Ok) { Write-Host "  [ok]   $What" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $What" -ForegroundColor Red; $script:failed++ }
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
$mode = if ($Verify) { ' (verify: read-only)' } elseif ($DryRun) { ' (dry run: no changes)' } else { '' }
Write-Host "Tenant $tenantId, signed in as $($me.userPrincipalName)$mode" -ForegroundColor Cyan

# ---------------------------------------------------------------------------------------------- verify
# Reads back every object and setting the steps below create. Same names, ids and parameters, so a check
# can only pass if a real run produced exactly this.
if ($Verify) {
    Write-Host "Support API app registration" -ForegroundColor Cyan
    $api = Find-One "applications" "$Prefix Support API"
    Assert-Check "app registration '$Prefix Support API' exists" ($null -ne $api)
    $apiSp = $null
    if ($api) {
        $apiUri = "api://$($api.appId)"
        Assert-Check "identifier URI is $apiUri" ($api.identifierUris -contains $apiUri)
        Assert-Check "issues v2 access tokens" ($api.api.requestedAccessTokenVersion -eq 2)
        Assert-Check "exposes the access_as_user scope" (@($api.api.oauth2PermissionScopes |
            Where-Object { $_.id -eq $scopeId -and $_.value -eq "access_as_user" -and $_.isEnabled }).Count -gt 0)
        foreach ($role in $roles) {
            $found = $api.appRoles | Where-Object { $_.id -eq $role.Id } | Select-Object -First 1
            Assert-Check "app role $($role.Value) (fixed id, enabled)" ($null -ne $found -and $found.value -eq $role.Value -and $found.isEnabled)
        }
        $cliPreAuthorised = @($api.api.preAuthorizedApplications |
            Where-Object { $_.appId -eq $azureCliAppId -and $_.delegatedPermissionIds -contains $scopeId }).Count -gt 0
        if ($AllowAzureCli) { Assert-Check "Azure CLI pre-authorised for access_as_user" $cliPreAuthorised }
        else { Assert-Check "Azure CLI not pre-authorised (run without -AllowAzureCli)" (-not $cliPreAuthorised) }

        $apiSp = Find-ServicePrincipal $api.appId
        Assert-Check "enterprise application exists" ($null -ne $apiSp)
        if ($apiSp) {
            Assert-Check "assignment required (no role, no token)" ($apiSp.appRoleAssignmentRequired -eq $true)
            Assert-Check "listed under Enterprise applications" ($apiSp.tags -contains $enterpriseAppTag)
        }
    }

    Write-Host "Support Dashboard app registration" -ForegroundColor Cyan
    $spa = Find-One "applications" "$Prefix Support Dashboard"
    Assert-Check "app registration '$Prefix Support Dashboard' exists" ($null -ne $spa)
    if ($spa) {
        foreach ($url in $SupportWebUrl) { Assert-Check "SPA redirect URI $url" ($spa.spa.redirectUris -contains $url) }
        if ($api) {
            Assert-Check "allowed to call the API (access_as_user)" (@($spa.requiredResourceAccess | Where-Object {
                $_.resourceAppId -eq $api.appId -and @($_.resourceAccess | Where-Object id -eq $scopeId).Count -gt 0 }).Count -gt 0)
        }
        $spaSp = Find-ServicePrincipal $spa.appId
        Assert-Check "enterprise application exists" ($null -ne $spaSp)
        if ($spaSp) { Assert-Check "listed under Enterprise applications" ($spaSp.tags -contains $enterpriseAppTag) }
        if ($spaSp -and $apiSp) {
            $grant = (Invoke-Graph GET "$graph/oauth2PermissionGrants?`$filter=$([uri]::EscapeDataString("clientId eq '$($spaSp.id)'"))").value |
                Where-Object { $_.resourceId -eq $apiSp.id -and $_.consentType -eq "AllPrincipals" } | Select-Object -First 1
            Assert-Check "admin consent granted for access_as_user" ($null -ne $grant -and ($grant.scope -split ' ') -contains "access_as_user")
        }
    }

    Write-Host "Security groups and role assignments" -ForegroundColor Cyan
    foreach ($role in $roles) {
        $group = Find-One "groups" $role.Group
        Assert-Check "group '$($role.Group)' exists" ($null -ne $group)
        $role.GroupId = if ($group) { $group.id } else { $null }
    }
    if ($apiSp) {
        $assigned = @((Invoke-Graph GET "$graph/servicePrincipals/$($apiSp.id)/appRoleAssignedTo").value)
        $hasRole = { param($PrincipalId, $Role) @($assigned | Where-Object { $_.principalId -eq $PrincipalId -and $_.appRoleId -eq $Role.Id }).Count -gt 0 }
        if ($AssignGroups) {
            foreach ($role in $roles) {
                if ($role.GroupId) { Assert-Check "group '$($role.Group)' has $($role.Value)" (& $hasRole $role.GroupId $role) }
            }
        } else {
            Assert-Check "you have $($adminRole.Value)" (& $hasRole $me.id $adminRole)
        }
    }

    # The end-to-end check: what the Support API will actually see.
    if ($AllowAzureCli -and $api) {
        Write-Host "Access token (decoded locally, never printed)" -ForegroundColor Cyan
        $ErrorActionPreference = "Continue"
        $token = az account get-access-token --scope "api://$($api.appId)/access_as_user" --query accessToken -o tsv 2>$null
        $ErrorActionPreference = "Stop"
        $gotToken = [bool]($LASTEXITCODE -eq 0 -and $token)
        Assert-Check "Azure CLI can get a token for the API" $gotToken
        if ($gotToken) {
            $claims = ConvertFrom-JwtPayload ("$token".Trim())
            Assert-Check "audience is the API's app id (Auth:Audience)" ($claims.aud -eq $api.appId)
            Assert-Check "issuer is this tenant's v2 endpoint (Auth:Authority)" ($claims.iss -eq "https://login.microsoftonline.com/$tenantId/v2.0")
            if ($AssignGroups) {
                # With groups, your roles depend on which groups you're in, which this script doesn't manage.
                Write-Host "  roles in the token: $(if ($claims.roles) { $claims.roles -join ', ' } else { '(none: add yourself to a group)' })"
            } else {
                Assert-Check "roles claim contains $($adminRole.Value)" (@($claims.roles) -contains $adminRole.Value)
            }
        }
    }

    Write-Host ""
    if ($script:failed -gt 0) {
        Write-Host "$($script:failed) check(s) failed. If you've just run the setup, wait a minute (Entra replication) and verify again." -ForegroundColor Red
        exit 1
    }
    Write-Host "All checks passed." -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------------------------- 1. API
Write-Host "1. Support API app registration" -ForegroundColor Cyan
$api = Get-OrCreateApp "$Prefix Support API"
$apiUri = "api://$($api.appId)"   # api://<appId> always passes the tenant's identifier-URI policy
$apiSettings = @{
    requestedAccessTokenVersion = 2   # v2 tokens: issuer https://login.microsoftonline.com/<tenant>/v2.0
    oauth2PermissionScopes = @(@{
        id = $scopeId; value = "access_as_user"; type = "User"; isEnabled = $true
        adminConsentDisplayName = "Use the SyncHub Support API"
        adminConsentDescription = "Lets the support dashboard call the SyncHub Support API as the signed-in user."
        userConsentDisplayName  = "Use the SyncHub Support API"
        userConsentDescription  = "Lets the support dashboard call the SyncHub Support API for you."
    })
    preAuthorizedApplications = @()   # set below with -AllowAzureCli; cleared when run without it
}
Invoke-Graph PATCH "$graph/applications/$($api.id)" @{
    identifierUris = @($apiUri)
    api = $apiSettings
    appRoles = @($roles | ForEach-Object {
        @{ id = $_.Id; value = $_.Value; displayName = $_.Name; description = $_.Description
           allowedMemberTypes = @("User"); isEnabled = $true }
    })
} | Out-Null
if ($AllowAzureCli) {
    # A second PATCH, so the scope is saved before a pre-authorisation refers to it.
    Write-Host "  pre-authorising the Azure CLI for access_as_user (test tokens)"
    $apiSettings.preAuthorizedApplications = @(@{ appId = $azureCliAppId; delegatedPermissionIds = @($scopeId) })
    Invoke-Graph PATCH "$graph/applications/$($api.id)" @{ api = $apiSettings } | Out-Null
}
$apiSp = Get-OrCreateServicePrincipal $api
# Only users or groups assigned a role can get a token for the API: no role, no access.
Invoke-Graph PATCH "$graph/servicePrincipals/$($apiSp.id)" @{
    appRoleAssignmentRequired = $true; tags = @($enterpriseAppTag) } | Out-Null

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
Invoke-Graph PATCH "$graph/servicePrincipals/$($spaSp.id)" @{ tags = @($enterpriseAppTag) } | Out-Null

# Admin consent for the dashboard to call the API on users' behalf (so users aren't prompted).
$existingGrant = if ($spaSp.id -like "<new-*") { $null } else {
    (Invoke-Graph GET "$graph/oauth2PermissionGrants?`$filter=$([uri]::EscapeDataString("clientId eq '$($spaSp.id)'"))").value |
        Where-Object { $_.resourceId -eq $apiSp.id } | Select-Object -First 1 }
if (-not $existingGrant) {
    Write-Host "  granting admin consent: dashboard -> API (access_as_user)"
    try {
        Invoke-Graph POST "$graph/oauth2PermissionGrants" @{
            clientId = $spaSp.id; consentType = "AllPrincipals"; resourceId = $apiSp.id; scope = "access_as_user"
        } | Out-Null
    } catch {
        # The only step that needs an admin role: ordinary members can register apps and (by default) create groups,
        # but tenant-wide consent needs Cloud Application Administrator or Global Administrator. Don't stop for it:
        # an admin can grant it with one button, and -Verify reports it until they do.
        Write-Warning ("Admin consent refused (needs Cloud Application Administrator or Global Administrator). " +
            "Grant it in the portal: Enterprise applications > $Prefix Support Dashboard > Permissions > Grant admin consent.")
    }
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
    <# Assigns an app role on the API to a user or group, unless it already has it. #>
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
    Grant-AppRole $me.id "you (the signed-in user)" $adminRole
}

# ---------------------------------------------------------------------------------------------- result
Write-Host ""
Write-Host "Settings for the apps (not secrets; keep them out of Git anyway, e.g. user secrets or Container App env vars):" -ForegroundColor Green
Write-Host "  Support API  Auth:Authority = https://login.microsoftonline.com/$tenantId/v2.0"
Write-Host "  Support API  Auth:Audience  = $($api.appId)"
Write-Host "  Dashboard    clientId       = $($spa.appId)"
Write-Host "  Dashboard    scope          = $apiUri/access_as_user"
Write-Host ""
$verifyArgs = "-Verify$(if ($AllowAzureCli) { ' -AllowAzureCli' })$(if ($AssignGroups) { ' -AssignGroups' })"
Write-Host "Next: check it with  .\infra\entra-setup.ps1 $verifyArgs  (wait a minute first if objects were just created)" -ForegroundColor Green
