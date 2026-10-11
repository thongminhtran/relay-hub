using './main.bicep'

param appName = 'relayhub'
param env = 'prod'
param sqlAdminLogin = 'relayhubadmin'
param sqlLocation = readEnvironmentVariable('SQL_LOCATION', 'canadacentral')

// Secrets come from the environment / pipeline, never from this file:
//   az deployment group create ... -p sqlAdminPassword=$SQL_ADMIN_PASSWORD apiKeyMasterKey=$API_KEY_MASTER_KEY deployerObjectId=$(az ad signed-in-user show --query id -o tsv)
param sqlAdminPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD', '')
param apiKeyMasterKey = readEnvironmentVariable('API_KEY_MASTER_KEY', '')
param deployerObjectId = readEnvironmentVariable('DEPLOYER_OBJECT_ID', '')
