// Merger Sync Hub on Azure: entry point.
//
// Deployed at subscription scope so it can create its own resource group: deleting that one group
// removes everything. Preview first (creates nothing):
//   az deployment sub what-if --location uksouth --template-file infra/main.bicep --parameters infra/main.bicepparam
// Two passes, because the Container Apps need images that only exist once the registry does:
//   1. deployApps = false: registry, SQL, Key Vault, monitoring, Container Apps environment
//   2. build the images in the registry (az acr build), load the databases, then deployApps = true
// See docs/azure-architecture.md for the design and infra/README.md for the steps.
targetScope = 'subscription'

@description('Azure region. UK South keeps a UK law firm\'s data in the UK.')
param location string = 'uksouth'

@description('Short name used in every resource name.')
@minLength(3)
@maxLength(10)
param prefix string = 'synchub'

@description('Entra ID object id of the person or group who administers Azure SQL (Entra-only authentication).')
param sqlAdminObjectId string

@description('Display name of that Entra admin (shown in the portal).')
param sqlAdminName string

@description('false for the first pass (no images yet); true once the images are in the registry.')
param deployApps bool = false

@description('Image tag to run.')
param imageTag string = 'latest'

@description('Monthly budget for the resource group, in the subscription\'s currency. Owners are emailed at 50%, 80% and 100%.')
param monthlyBudget int = 20

@description('Budget start: the first day of the current month, yyyy-MM-01.')
param budgetStartDate string = '${utcNow('yyyy-MM')}-01'

var tags = {
  project: 'merger-sync-hub'
  environment: 'demo'
  owner: 'interview-portfolio'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${prefix}'
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'synchub-resources'
  scope: rg
  params: {
    location: location
    prefix: prefix
    tags: tags
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminName: sqlAdminName
    deployApps: deployApps
    imageTag: imageTag
    monthlyBudget: monthlyBudget
    budgetStartDate: budgetStartDate
  }
}

output resourceGroup string = rg.name
output registryLoginServer string = resources.outputs.registryLoginServer
output sqlServerFqdn string = resources.outputs.sqlServerFqdn
output containerAppsDomain string = resources.outputs.containerAppsDomain
output identities object = resources.outputs.identities
output urls object = resources.outputs.urls
