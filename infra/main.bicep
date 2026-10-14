// RelayHub infrastructure — resource-group scope. All free tiers; see docs/plan-src/plan.md section 10.
// Week 2 adds the Functions Flex Consumption worker, the poison queues and the operator-alert secret.
// Weeks 3/4 add Azure SignalR Service (serverless, Free_F1), the Static Web App for the dashboard and the
// auth / channel secrets (JWT signing key, owner login, Turnstile, Meta, Telegram, inbound email, Resend).
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

@description('Region for the SQL server. Free-trial subscriptions are refused SQL servers in some regions (Canada East on 2026-10-11), while App Service F1 quota is 0 in others (Canada Central). Mixing is fine: ~10 ms between the Canadian regions.')
param sqlLocation string = location

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

@description('Region for the Functions Flex Consumption plan (the worker). Not every region offers Flex; Canada Central does.')
param functionsLocation string = 'canadacentral'

@secure()
@description('Optional Discord incoming-webhook URL for operator alerts (dead-letters, poison messages). Empty = alerts only go to the logs.')
param opsDiscordWebhookUrl string = ''

// ---- Week 3: dashboard auth (required) ----
@secure()
@minLength(32)
@description('HS256 key that signs the dashboard JWTs (RelayHub:JwtSigningKey). At least 32 characters; rotating it logs everyone out.')
param jwtSigningKey string

@secure()
@minLength(3)
@description('Login of the owner user the seeder creates (RelayHub:OwnerEmail).')
param ownerEmail string

@secure()
@minLength(8)
@description('Password of the owner user (RelayHub:OwnerPassword). The seeder re-hashes it on startup, so a rotated value works after a redeploy.')
param ownerPassword string

// ---- Weeks 3/4: optional feature secrets. Empty = the feature answers 503 "not configured" and logs one warning at startup. ----
@secure()
@description('Cloudflare Turnstile secret for the demo login (RelayHub:Turnstile:Secret). Empty = any/no token is accepted.')
param turnstileSecret string = ''

@secure()
@description('Meta app secret that signs Facebook/Instagram webhooks (RelayHub:Meta:AppSecret).')
param metaAppSecret string = ''

@secure()
@description('Verify token Meta sends on webhook subscription (RelayHub:Meta:VerifyToken).')
param metaVerifyToken string = ''

@secure()
@description('Secret the Telegram bot webhook sends in X-Telegram-Bot-Api-Secret-Token (RelayHub:Telegram:WebhookSecret).')
param telegramWebhookSecret string = ''

@secure()
@description('HMAC secret shared with the Cloudflare Email Worker that posts inbound mail (RelayHub:Email:InboundSecret).')
param emailInboundSecret string = ''

@secure()
@description('Resend API key for outbound email (RelayHub:Resend:ApiKey). Used by the API (replies) and the worker (email destinations).')
param resendApiKey string = ''

@description('Default sender for Resend (RelayHub:Resend:From), e.g. "RelayHub <inbox@example.com>". Not a secret: a plain app setting.')
param resendFrom string = ''

// ---- Weeks 3/4: realtime + dashboard hosting ----
@description('Deploy Azure SignalR Service. Off by default: the subscription must first register the Microsoft.SignalRService resource provider (portal → Subscription → Resource providers → Register; the GitHub deploy identity is only a Contributor on the resource group and cannot do it). Without it the API answers 503 realtime-unavailable and the dashboard shows a Refresh button.')
param deploySignalR bool = false

@description('Region for Azure SignalR Service. Defaults to the API region here; main.bicepparam puts it in canadacentral next to SQL and the worker (env SIGNALR_LOCATION).')
param signalRLocation string = location

@description('Region for the Static Web App resource (its content is served from a global edge). Static Web Apps exists only in a few regions — westus2, centralus, eastus2, westeurope, eastasia — not in Canada; the Azure CLI default is centralus.')
param staticWebAppLocation string = 'eastus2'

var suffix = toLower('${appName}-${env}')
// Key Vault, storage and SQL server names are globally unique across all of Azure: add a stable per-subscription suffix.
var unique = take(uniqueString(subscription().subscriptionId, resourceGroup().id), 6)
var tags = { project: 'relayhub', env: env, 'cost-center': 'free-tier' }

// ---------- Monitoring (free: 5 GB/month; capped at 0.1 GB/day) ----------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${suffix}-${unique}' // workspace names stay reserved for 14 days after deletion (soft delete)
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
  name: 'kv-${appName}-${unique}'
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

resource secretOpsWebhook 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(opsDiscordWebhookUrl)) {
  parent: keyVault
  name: 'RelayHub--OpsDiscordWebhookUrl'
  properties: { value: opsDiscordWebhookUrl }
}

// App setting shared by the API and the worker; only present when the secret exists (a Key Vault reference to a missing secret
// is handed to the app as the literal reference string, which the alert sender would then try to POST to).
var opsWebhookSettings = empty(opsDiscordWebhookUrl) ? [] : [
  { name: 'RelayHub__OpsDiscordWebhookUrl', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=RelayHub--OpsDiscordWebhookUrl)' }
]

// ---- Week 3: dashboard auth secrets (required; the API's seeder creates the owner user from the two owner settings) ----
resource secretJwtSigningKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'RelayHub--JwtSigningKey'
  properties: { value: jwtSigningKey }
}

resource secretOwnerEmail 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'RelayHub--OwnerEmail'
  properties: { value: ownerEmail }
}

resource secretOwnerPassword 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'RelayHub--OwnerPassword'
  properties: { value: ownerPassword }
}

// ---- Weeks 3/4: optional feature secrets, deployed only when a value was supplied ----
resource secretTurnstile 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(turnstileSecret)) {
  parent: keyVault
  name: 'RelayHub--Turnstile--Secret'
  properties: { value: turnstileSecret }
}

resource secretMetaAppSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(metaAppSecret)) {
  parent: keyVault
  name: 'RelayHub--Meta--AppSecret'
  properties: { value: metaAppSecret }
}

resource secretMetaVerifyToken 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(metaVerifyToken)) {
  parent: keyVault
  name: 'RelayHub--Meta--VerifyToken'
  properties: { value: metaVerifyToken }
}

resource secretTelegramWebhook 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(telegramWebhookSecret)) {
  parent: keyVault
  name: 'RelayHub--Telegram--WebhookSecret'
  properties: { value: telegramWebhookSecret }
}

resource secretEmailInbound 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(emailInboundSecret)) {
  parent: keyVault
  name: 'RelayHub--Email--InboundSecret'
  properties: { value: emailInboundSecret }
}

resource secretResendApiKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (!empty(resendApiKey)) {
  parent: keyVault
  name: 'RelayHub--Resend--ApiKey'
  properties: { value: resendApiKey }
}

// Same rule as opsWebhookSettings, for every optional secret: the Key Vault reference is only emitted when the secret is deployed.
// `set` is a boolean, so no secret value ever lands in a variable. (Key Vault secret `A--B` ↔ app setting `A__B` ↔ configuration `A:B`.)
var optionalSecretSettings = [
  { name: 'RelayHub__Turnstile__Secret', secret: 'RelayHub--Turnstile--Secret', set: !empty(turnstileSecret), api: true, worker: false }
  { name: 'RelayHub__Meta__AppSecret', secret: 'RelayHub--Meta--AppSecret', set: !empty(metaAppSecret), api: true, worker: false }
  { name: 'RelayHub__Meta__VerifyToken', secret: 'RelayHub--Meta--VerifyToken', set: !empty(metaVerifyToken), api: true, worker: false }
  { name: 'RelayHub__Telegram__WebhookSecret', secret: 'RelayHub--Telegram--WebhookSecret', set: !empty(telegramWebhookSecret), api: true, worker: false }
  { name: 'RelayHub__Email__InboundSecret', secret: 'RelayHub--Email--InboundSecret', set: !empty(emailInboundSecret), api: true, worker: false }
  { name: 'RelayHub__Resend__ApiKey', secret: 'RelayHub--Resend--ApiKey', set: !empty(resendApiKey), api: true, worker: true }
]
var apiOptionalSecretSettings = map(filter(optionalSecretSettings, s => s.set && s.api), s => {
  name: s.name
  value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${s.secret})'
})
var workerOptionalSecretSettings = map(filter(optionalSecretSettings, s => s.set && s.worker), s => {
  name: s.name
  value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${s.secret})'
})
// Not a secret, but equally optional: an empty RelayHub:Resend:From would override the configured default with "".
var resendFromSettings = empty(resendFrom) ? [] : [{ name: 'RelayHub__Resend__From', value: resendFrom }]

// ---------- Data: Azure SQL free offer (serverless, auto-pause until next month when the free limit is used up) ----------
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-${suffix}-${unique}'
  location: sqlLocation
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
  location: sqlLocation
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
  name: toLower(replace('st${appName}${unique}', '-', ''))
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

// The Functions host parks a message here after it crashed the function maxDequeueCount times; PoisonAlerts watches both.
resource queueIncomingPoison 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queues
  name: 'incoming-poison'
}

resource queueDeliveriesPoison 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queues
  name: 'deliveries-poison'
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

// Flex Consumption keeps the deployed package in a blob container (accessed with the worker's managed identity).
resource workerPackageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'app-package-worker'
}

// ---------- Dashboard hosting: Static Web App, Free tier (100 GB bandwidth/month, 0.25 GB app size, free TLS) ----------
// No repository binding: deploy.yml builds web/ itself and uploads web/dist with the deployment token it reads from this
// resource (az staticwebapp secrets list), so no extra GitHub secret is needed. The SPA fallback lives in web/public/staticwebapp.config.json.
resource swa 'Microsoft.Web/staticSites@2024-04-01' = {
  name: 'stapp-${suffix}'
  location: staticWebAppLocation
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: {
    provider: 'Custom' // content comes from our own pipeline, not from a linked GitHub/ADO repo
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Enabled'
  }
}

// ---------- Realtime: Azure SignalR Service, Free_F1 (1 unit: 20 concurrent connections, 20k messages/day), Serverless mode ----------
// Serverless = no hub server: the API answers /api/v1/realtime/negotiate and both apps push through the Management SDK (REST),
// authenticated with their managed identities (SignalR__Endpoint only, no connection string). Local auth (access keys) stays on
// so a developer can still use SignalR:ConnectionString locally; nothing in Azure uses the keys.
resource signalr 'Microsoft.SignalRService/signalR@2024-03-01' = if (deploySignalR) {
  name: 'sigr-${suffix}-${unique}' // the hostname <name>.service.signalr.net is global, like the Key Vault / storage names
  location: signalRLocation
  tags: tags
  kind: 'SignalR'
  sku: { name: 'Free_F1', tier: 'Free', capacity: 1 }
  properties: {
    features: [
      { flag: 'ServiceMode', value: 'Serverless' }
    ]
    // Browser clients talk to the service directly after negotiate: only the dashboard origin may do so.
    cors: { allowedOrigins: ['https://${swa.properties.defaultHostname}'] }
    publicNetworkAccess: 'Enabled'
    tls: { clientCertEnabled: false }
  }
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
  name: 'app-${suffix}-api-${unique}'
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
      appSettings: concat([
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        // App Service terminates TLS in front of Kestrel: without this every client shares the front-end IP and the per-IP rate limits would be global.
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__RelayHub', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretSqlConnection.name})' }
        { name: 'RelayHub__ApiKeyMasterKey', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretMasterKey.name})' }
        { name: 'RelayHub__MigrateOnStartup', value: 'false' }
        { name: 'RelayHub__SeedOnStartup', value: 'true' } // the seeder also creates/updates the owner user from the two owner secrets
        { name: 'Storage__QueueServiceUri', value: 'https://${storage.name}.queue.${environment().suffixes.storage}' }
        // Week 3: dashboard auth + CORS + realtime
        { name: 'RelayHub__JwtSigningKey', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretJwtSigningKey.name})' }
        { name: 'RelayHub__OwnerEmail', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretOwnerEmail.name})' }
        { name: 'RelayHub__OwnerPassword', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretOwnerPassword.name})' }
        { name: 'RelayHub__DashboardOrigin', value: 'https://${swa.properties.defaultHostname}' }
      ], signalRSettings, opsWebhookSettings, apiOptionalSecretSettings, resendFromSettings)
    }
  }
  // The optional secrets are only referenced by name inside the settings: make sure they exist before the app first resolves them.
  dependsOn: [
    secretTurnstile
    secretMetaAppSecret
    secretMetaVerifyToken
    secretTelegramWebhook
    secretEmailInbound
    secretResendApiKey
  ]
}

// ---------- Compute: Functions Flex Consumption for the worker (free grant: 250k executions + 100k GB-s / month) ----------
resource workerPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${suffix}-worker'
  location: functionsLocation
  tags: tags
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource worker 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${suffix}-worker-${unique}'
  location: functionsLocation
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: workerPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${workerPackageContainer.name}'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: concat([
        // Host storage (locks, poison queues) and the queue triggers: identity-based, no connection string anywhere.
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__RelayHub', value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${secretSqlConnection.name})' }
        { name: 'Storage__QueueServiceUri', value: 'https://${storage.name}.queue.${environment().suffixes.storage}' }
      ], signalRSettings, opsWebhookSettings, workerOptionalSecretSettings, resendFromSettings)
    }
  }
  dependsOn: [secretResendApiKey]
}

// ---------- RBAC: the API's managed identity reads secrets and uses the queues ----------
var roleKeyVaultSecretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var roleKeyVaultSecretsOfficer = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
var roleStorageQueueDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var roleStorageBlobDataOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var roleStorageAccountContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '17d1049b-9a84-46fb-8f53-869881c3d3ab')

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

// ---------- RBAC: the worker's managed identity — secrets, host storage (blob + queue; no tables: no Durable Functions) and the deployment container ----------
resource workerReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, worker.id, roleKeyVaultSecretsUser)
  scope: keyVault
  properties: { roleDefinitionId: roleKeyVaultSecretsUser, principalId: worker.identity.principalId, principalType: 'ServicePrincipal' }
}

resource workerOwnsBlobs 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, worker.id, roleStorageBlobDataOwner)
  scope: storage
  properties: { roleDefinitionId: roleStorageBlobDataOwner, principalId: worker.identity.principalId, principalType: 'ServicePrincipal' }
}

resource workerUsesQueues 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, worker.id, roleStorageQueueDataContributor)
  scope: storage
  properties: { roleDefinitionId: roleStorageQueueDataContributor, principalId: worker.identity.principalId, principalType: 'ServicePrincipal' }
}

resource workerManagesStorageAccount 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, worker.id, roleStorageAccountContributor)
  scope: storage
  properties: { roleDefinitionId: roleStorageAccountContributor, principalId: worker.identity.principalId, principalType: 'ServicePrincipal' }
}

// Both apps read SignalR__Endpoint only when the resource is deployed (the Management SDK is skipped and the NullRealtimeNotifier stays otherwise).
var signalRSettings = deploySignalR ? [
  { name: 'SignalR__Endpoint', value: 'https://${signalr!.properties.hostName}' }
] : []

// ---------- RBAC: both identities push to SignalR with DefaultAzureCredential (Management SDK, serverless mode) ----------
// Role id copied from https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/web-and-mobile#signalr-service-owner
// (never typed from memory — see the build journal). Which role: https://learn.microsoft.com/azure/azure-signalr/signalr-howto-authorize-managed-identity
// says "SignalR Service Owner" (data action Microsoft.SignalRService/SignalR/*) is the one for "a negotiation server with an
// Azure SignalR Service resource running in Serverless mode. It requires both REST API permissions and authentication API
// permissions". The narrower "SignalR REST API Owner" (fd53cd77-2268-407a-8f46-7e7863d0f521) lacks SignalR/auth/accessKey/action,
// which the SDK calls to obtain the key it signs REST and client tokens with, and "SignalR App Server"
// (420fcaa2-552c-430f-98ca-3264be4806c7) lacks hub/group/user, so neither alone is enough for negotiate + send.
var roleSignalRServiceOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7e4f1700-ea5a-4f59-8f37-079cfe29dce3')

resource apiUsesSignalR 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySignalR) {
  name: guid(signalr.id, api.id, roleSignalRServiceOwner)
  scope: signalr
  properties: { roleDefinitionId: roleSignalRServiceOwner, principalId: api.identity.principalId, principalType: 'ServicePrincipal' }
}

resource workerUsesSignalR 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deploySignalR) {
  name: guid(signalr.id, worker.id, roleSignalRServiceOwner)
  scope: signalr
  properties: { roleDefinitionId: roleSignalRServiceOwner, principalId: worker.identity.principalId, principalType: 'ServicePrincipal' }
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
output workerName string = worker.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDb.name
output keyVaultName string = keyVault.name
output storageAccountName string = storage.name
output appInsightsName string = appInsights.name
output signalRName string = deploySignalR ? signalr!.name : ''
output signalREndpoint string = deploySignalR ? 'https://${signalr!.properties.hostName}' : ''
output staticWebAppName string = swa.name
output dashboardHostname string = swa.properties.defaultHostname
output dashboardUrl string = 'https://${swa.properties.defaultHostname}'
