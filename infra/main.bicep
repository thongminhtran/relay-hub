// RelayHub infrastructure — resource-group scope. All free tiers; see docs/plan-src/plan.md section 10.
// Deploy: az deployment group create -g rg-relayhub -f infra/main.bicep -p infra/main.bicepparam
targetScope = 'resourceGroup'

@description('Short name used as a prefix for every resource.')
@minLength(3)
@maxLength(12)
param appName string = 'relayhub'

@description('Azure region. Pick one where the Azure SQL free offer is available (e.g. canadacentral, eastus2).')
param location string = resourceGroup().location

@description('Environment suffix, e.g. prod.')
param env string = 'prod'

@description('SQL administrator login (SQL auth is a Week 1 stop-gap; Entra-only auth with Managed Identity arrives in Week 2).')
param sqlAdminLogin string = 'relayhubadmin'

@secure()
@description('SQL administrator password. Stored in Key Vault; never in app settings.')
param sqlAdminPassword string

@secure()
@description('Master key used to sign source API keys (RelayHub:ApiKeyMasterKey).')
param apiKeyMasterKey string

@description('Object id of the person who deploys locally (gets Key Vault Secrets Officer). Leave empty in CI.')
param deployerObjectId string = ''

var suffix = toLower('${appName}-${env}')
var tags = { project: 'relayhub', env: env, 'cost-center': 'free-tier' }

// ---------- Monitoring (free: 5 GB/month; capped at 0.1 GB/day) ----------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    IngestionMode: 'LogAnalytics'
  }
}

// ---------- Secrets ----------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

resource secretSqlConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ConnectionStrings--RelayHub'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDb.name};User Id=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60'
  }
}

resource secretMasterKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'RelayHub--ApiKeyMasterKey'
  properties: { value: apiKeyMasterKey }
}

// ---------- Data: Azure SQL free offer (serverless, auto-pause until next month when the free limit is used up) ----------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${suffix}'
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'relayhub'
  location: location
  tags: tags
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// ---------- Messaging: Storage account + queues (Week 2 worker) ----------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: toLower(replace('st${appName}${env}', '-', ''))
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource queues 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource queueIncoming 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queues
  name: 'incoming'
}

resource queueDeliveries 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queues
  name: 'deliveries'
}

// ---------- Compute: App Service F1 (Linux) for the API ----------
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${suffix}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: 'F1', tier: 'Free' }
  properties: { reserved: true }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: 'app-${suffix}-api'
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: false // not available on F1
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__RelayHub', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretSqlConnection.name})' }
        { name: 'RelayHub__ApiKeyMasterKey', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretMasterKey.name})' }
        { name: 'RelayHub__MigrateOnStartup', value: 'false' }
        { name: 'RelayHub__SeedOnStartup', value: 'true' }
        { name: 'Storage__QueueServiceUri', value: 'https://${storage.name}.queue.${environment().suffixes.storage}' }
      ]
    }
  }
}

// ---------- RBAC: the API's managed identity reads secrets and uses the queues ----------
var roleKeyVaultSecretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var roleKeyVaultSecretsOfficer = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
var roleStorageQueueDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')

resource apiReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, api.id, roleKeyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: roleKeyVaultSecretsUser
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apiUsesQueues 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, api.id, roleStorageQueueDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: roleStorageQueueDataContributor
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployerManagesSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerObjectId)) {
  name: guid(keyVault.id, deployerObjectId, roleKeyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: roleKeyVaultSecretsOfficer
    principalId: deployerObjectId
    principalType: 'User'
  }
}

output apiName string = api.name
output apiUrl string = 'https://${api.properties.defaultHostName}'
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDb.name
output keyVaultName string = keyVault.name
output storageAccountName string = storage.name
output appInsightsName string = appInsights.name
