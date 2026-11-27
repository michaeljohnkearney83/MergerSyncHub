// Parameters for main.bicep. The SQL admin is read from environment variables so no personal id is committed:
//   $env:SQL_ADMIN_OBJECT_ID = az ad signed-in-user show --query id -o tsv
//   $env:SQL_ADMIN_NAME      = az ad signed-in-user show --query userPrincipalName -o tsv
using 'main.bicep'

param location = 'uksouth'
param prefix = 'synchub'
param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID')
param sqlAdminName = readEnvironmentVariable('SQL_ADMIN_NAME')
param deployApps = bool(readEnvironmentVariable('DEPLOY_APPS', 'false'))
param imageTag = readEnvironmentVariable('IMAGE_TAG', 'latest')
param monthlyBudget = 20
