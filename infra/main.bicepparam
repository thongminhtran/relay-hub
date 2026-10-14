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
param functionsLocation = readEnvironmentVariable('FUNCTIONS_LOCATION', 'canadacentral')
param opsDiscordWebhookUrl = readEnvironmentVariable('OPS_DISCORD_WEBHOOK_URL', '')

// Week 3 — required (GitHub secrets JWT_SIGNING_KEY, OWNER_EMAIL, OWNER_PASSWORD). No default on purpose: a missing variable
// fails the deployment while compiling this file ("environment variable ... does not exist"), before anything is touched.
param jwtSigningKey = readEnvironmentVariable('JWT_SIGNING_KEY')
param ownerEmail = readEnvironmentVariable('OWNER_EMAIL')
param ownerPassword = readEnvironmentVariable('OWNER_PASSWORD')

// Weeks 3/4 — optional; empty = the secret is not created and the app setting is not emitted.
param turnstileSecret = readEnvironmentVariable('TURNSTILE_SECRET', '')
param metaAppSecret = readEnvironmentVariable('META_APP_SECRET', '')
param metaVerifyToken = readEnvironmentVariable('META_VERIFY_TOKEN', '')
param telegramWebhookSecret = readEnvironmentVariable('TELEGRAM_WEBHOOK_SECRET', '')
param emailInboundSecret = readEnvironmentVariable('EMAIL_INBOUND_SECRET', '')
param resendApiKey = readEnvironmentVariable('RESEND_API_KEY', '')
param resendFrom = readEnvironmentVariable('RESEND_FROM', '')

// Weeks 3/4 — regions. SignalR goes next to SQL and the worker (Canada Central already accepted every tier on this subscription);
// Static Web Apps is not offered in Canada at all (westus2, centralus, eastus2, westeurope, eastasia), and its content is global anyway.
param deploySignalR = toLower(readEnvironmentVariable('DEPLOY_SIGNALR', 'false')) == 'true' // an unset repo variable arrives as an empty string, so no bool() cast // repo variable DEPLOY_SIGNALR=true once Microsoft.SignalRService is registered on the subscription
param signalRLocation = readEnvironmentVariable('SIGNALR_LOCATION', 'canadacentral')
param staticWebAppLocation = readEnvironmentVariable('STATIC_WEB_APP_LOCATION', 'eastus2')
