// Everything inside the resource group. Called by main.bicep.
//
// Identity model: three user-assigned managed identities (worker, Support API, directory), each with only
// what it needs: AcrPull on the registry, Key Vault Secrets User on the vault, and a contained user in the
// databases it uses (created by infra/deploy-database.ps1, because SQL users can't be created from ARM).
// No passwords: Azure SQL is Entra-only, the registry has no admin user, secrets come from Key Vault.

param location string
param prefix string
param tags object
param sqlAdminObjectId string
param sqlAdminName string
param deployApps bool
param imageTag string
param monthlyBudget int
param budgetStartDate string

@description('Shared key between the worker and the Firm B mock. Generated per deployment; stored only in Key Vault.')
@secure()
param firmBApiKey string = newGuid()

// Globally unique names (registry, vault, SQL server) get a short hash of the resource group id.
var suffix = take(uniqueString(resourceGroup().id), 6)
var registryName = toLower('acr${prefix}${suffix}')
var vaultName = 'kv-${prefix}-${suffix}'
var sqlServerName = 'sql-${prefix}-${suffix}'

/* ----------------------------------------------------------------- monitoring */

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${prefix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: 1 }   // a hard cap, so a logging bug can't run up a bill
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${prefix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

/* ----------------------------------------------------------------- identities */

var identityNames = [ 'worker', 'support-api', 'directory' ]

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for name in identityNames: {
  name: 'id-${prefix}-${name}'
  location: location
  tags: tags
}]

/* ----------------------------------------------------------------- registry */

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false   // pulls use managed identity; no registry password exists
  }
}

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in identityNames: {
  name: guid(registry.id, identities[i].id, acrPullRoleId)
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identities[i].properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

/* ----------------------------------------------------------------- key vault */

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true   // access by Azure roles, not vault access policies
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource firmBApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'firmb-api-key'
  properties: { value: firmBApiKey }
}
// directory-signing-key is added by infra/deploy-database.ps1 (an RSA key can't be generated in Bicep).

var kvSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in identityNames: {
  name: guid(vault.id, identities[i].id, kvSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: identities[i].properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

/* ----------------------------------------------------------------- azure sql */

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'   // demo; production: private endpoint and 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true  // no SQL logins at all: no sa, no passwords
      login: sqlAdminName
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      principalType: 'User'
    }
  }
}

// "Allow Azure services": lets the Container Apps reach SQL without a VNet. Production uses a private endpoint.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// Serverless with the Azure SQL free offer: no charge within the monthly allowance per database, and the
// database pauses (rather than billing) if the allowance runs out. FirmA_Legacy stands in for the
// on-premises legacy database, which in production would stay on-premises behind a VPN.
var databaseNames = [ 'SyncHub', 'CorpDirectory', 'FirmA_Legacy' ]

resource databases 'Microsoft.Sql/servers/databases@2023-08-01-preview' = [for name in databaseNames: {
  parent: sqlServer
  name: name
  location: location
  tags: tags
  sku: { name: 'GP_S_Gen5_1', tier: 'GeneralPurpose', family: 'Gen5', capacity: 1 }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368   // 32 GB, the free-offer limit
    zoneRedundant: false        // demo; production: true
    requestedBackupStorageRedundancy: 'Local'
  }
}]

/* ----------------------------------------------------------------- container apps */

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${prefix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
  }
}

var domain = environment.properties.defaultDomain
var url = {
  directoryApi: 'https://${prefix}-directory-api.${domain}'
  supportApi: 'https://${prefix}-support-api.${domain}'
  firmbApi: 'https://${prefix}-firmb-api.${domain}'
  supportWeb: 'https://${prefix}-support-web.${domain}'
  directoryWeb: 'https://${prefix}-directory-web.${domain}'
}

var sqlFqdn = sqlServer.properties.fullyQualifiedDomainName
// Managed identity sign-in to Azure SQL: no password. User Id is the identity's client id.
var sqlConnection = 'Server=tcp:${sqlFqdn},1433;Encrypt=True;Authentication=Active Directory Managed Identity'

var common = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Azure' }
  { name: 'DOTNET_ENVIRONMENT', value: 'Azure' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  { name: 'OpenTelemetry__OtlpEndpoint', value: '' }   // Azure Monitor exporter instead of OTLP (see SyncHub.Observability)
  // TLS ends at the Container Apps ingress. This built-in switch makes ASP.NET Core trust X-Forwarded-Proto,
  // so the directory's discovery document advertises https:// URLs rather than the internal http://.
  { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
]

// Fixed list for the loop (Bicep needs it before deployment starts); per-app settings that depend on
// deployed resources are looked up by name inside the loop.
var appSpecs = [
  { name: 'directory-api', identity: 2, minReplicas: 1, keyVolume: true }   // 1: keeps sign-in snappy for the demo
  { name: 'support-api',   identity: 1, minReplicas: 0, keyVolume: false }
  { name: 'firmb-api',     identity: 0, minReplicas: 0, keyVolume: false }
  { name: 'sync-worker',   identity: 0, minReplicas: 1, keyVolume: false }   // no ingress; one instance
  { name: 'support-web',   identity: 0, minReplicas: 0, keyVolume: false }
  { name: 'directory-web', identity: 0, minReplicas: 0, keyVolume: false }
]

var appEnv = {
  'directory-api': concat(common, [
    { name: 'ConnectionStrings__Directory', value: '${sqlConnection};Database=CorpDirectory;User Id=${identities[2].properties.clientId}' }
    { name: 'Directory__Issuer', value: url.directoryApi }
    { name: 'Directory__SigningKeyPath', value: '/keys/signing-key.pem' }
    { name: 'Cors__Origins__0', value: url.supportWeb }
    { name: 'Cors__Origins__1', value: url.directoryWeb }
  ])
  'support-api': concat(common, [
    { name: 'ConnectionStrings__SyncHub', value: '${sqlConnection};Database=SyncHub;User Id=${identities[1].properties.clientId}' }
    { name: 'Auth__Authority', value: url.directoryApi }
    { name: 'Dashboard__Origin', value: url.supportWeb }
  ])
  'firmb-api': concat(common, [ { name: 'ApiKey', secretRef: 'firmb-api-key' } ])
  'sync-worker': concat(common, [
    { name: 'ConnectionStrings__SyncHub', value: '${sqlConnection};Database=SyncHub;User Id=${identities[0].properties.clientId}' }
    { name: 'ConnectionStrings__FirmA', value: '${sqlConnection};Database=FirmA_Legacy;User Id=${identities[0].properties.clientId};ApplicationIntent=ReadOnly' }
    { name: 'FirmB__BaseUrl', value: '${url.firmbApi}/' }
    { name: 'FirmB__ApiKey', secretRef: 'firmb-api-key' }
  ])
  'support-web': []
  'directory-web': []
}

var firmBSecret = [ { name: 'firmb-api-key', keyVaultUrl: firmBApiKeySecret.properties.secretUri, identity: identities[0].id } ]
var appSecrets = {
  'directory-api': [ { name: 'signing-key', keyVaultUrl: '${vault.properties.vaultUri}secrets/directory-signing-key', identity: identities[2].id } ]
  'support-api': []
  'firmb-api': firmBSecret
  'sync-worker': firmBSecret
  'support-web': []
  'directory-web': []
}

// Image names in the registry match the compose service names (built by infra/deploy-database.ps1 / az acr build).
resource containerApps 'Microsoft.App/containerApps@2024-03-01' = [for app in appSpecs: if (deployApps) {
  name: '${prefix}-${app.name}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities[app.identity].id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: app.name == 'sync-worker' ? null : {
        external: true
        targetPort: contains(app.name, '-web') ? 80 : 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [ { server: registry.properties.loginServer, identity: identities[app.identity].id } ]
      secrets: appSecrets[app.name]
    }
    template: {
      containers: [ {
        name: app.name
        image: '${registry.properties.loginServer}/${app.name}:${imageTag}'
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: appEnv[app.name]
        volumeMounts: app.keyVolume ? [ { volumeName: 'keys', mountPath: '/keys' } ] : []
      } ]
      volumes: app.keyVolume ? [ { name: 'keys', storageType: 'Secret', secrets: [ { secretRef: 'signing-key', path: 'signing-key.pem' } ] } ] : []
      scale: {
        minReplicas: app.minReplicas
        maxReplicas: app.name == 'sync-worker' ? 1 : 2   // the worker's watermark logic assumes one instance
      }
    }
  }
  dependsOn: [ acrPull, kvSecretsUser ]
}]

/* ----------------------------------------------------------------- budget */

resource budget 'Microsoft.Consumption/budgets@2023-05-01' = {
  name: 'budget-${prefix}'
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual50: { enabled: true, operator: 'GreaterThan', threshold: 50, contactEmails: [], contactRoles: [ 'Owner' ] }
      actual80: { enabled: true, operator: 'GreaterThan', threshold: 80, contactEmails: [], contactRoles: [ 'Owner' ] }
      forecast100: { enabled: true, operator: 'GreaterThan', threshold: 100, thresholdType: 'Forecasted', contactEmails: [], contactRoles: [ 'Owner' ] }
    }
  }
}

/* ----------------------------------------------------------------- outputs */

output registryLoginServer string = registry.properties.loginServer
output registryName string = registry.name
output vaultName string = vault.name
output sqlServerFqdn string = sqlFqdn
output containerAppsDomain string = domain
output identities object = {
  worker: { name: identities[0].name, clientId: identities[0].properties.clientId }
  supportApi: { name: identities[1].name, clientId: identities[1].properties.clientId }
  directory: { name: identities[2].name, clientId: identities[2].properties.clientId }
}
output urls object = url
