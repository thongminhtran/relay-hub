<#
.SYNOPSIS
  One-time Azure bootstrap for RelayHub: resource group, budget alert, Bicep deploy, GitHub Actions OIDC.
  Idempotent: safe to re-run. Requires: az login (subscription selected), gh auth login.

.EXAMPLE
  .\infra\bootstrap.ps1 -BudgetEmail you@example.com
  .\infra\bootstrap.ps1 -BudgetEmail you@example.com -Location eastus2
  .\infra\bootstrap.ps1 -BudgetEmail you@example.com -OwnerEmail you@example.com -OwnerPassword '<the dashboard login you want>'

.NOTES
  Secrets (SQL password, API-key master key, JWT signing key, owner password) are taken from the environment variables named in
  infra/main.bicepparam when set, otherwise generated. Re-running WITHOUT those variables therefore ROTATES them: a new master key
  invalidates every source API key, a new JWT key logs every dashboard user out. Optional channel secrets (TURNSTILE_SECRET,
  META_APP_SECRET, META_VERIFY_TOKEN, TELEGRAM_WEBHOOK_SECRET, EMAIL_INBOUND_SECRET, RESEND_API_KEY, RESEND_FROM) are forwarded
  to the deployment and to GitHub only when present in the environment.
#>
param(
    [Parameter(Mandatory = $true)] [string] $BudgetEmail,
    [string] $OwnerEmail = "",          # dashboard owner login; defaults to -BudgetEmail
    [string] $OwnerPassword = "",       # dashboard owner password; defaults to $env:OWNER_PASSWORD, else generated and shown ONCE
    [string] $Location = "canadaeast",
    [string] $SqlLocation = "canadacentral",
    [string] $SignalRLocation = "canadacentral",
    [string] $StaticWebAppLocation = "eastus2",   # Static Web Apps is not offered in Canada (westus2, centralus, eastus2, westeurope, eastasia)
    [string] $ResourceGroup = "rg-relayhub",
    [string] $Repo = "thongminhtran/relay-hub",
    [string] $GitHubAppName = "relayhub-github-deploy",
    [int] $BudgetAmount = 5
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Az {
    param([string[]] $AzArgs)
    # stdout only: az prints welcome banners / warnings on stderr, which would corrupt JSON output.
    $errFile = Join-Path $env:TEMP ("relayhub-az-" + [guid]::NewGuid().ToString("N") + ".log")
    $out = & az @AzArgs 2> $errFile
    $code = $LASTEXITCODE
    $err = if (Test-Path $errFile) { Get-Content $errFile -Raw; Remove-Item $errFile -ErrorAction SilentlyContinue } else { "" }
    if ($code -ne 0) { throw "az $($AzArgs -join ' ') failed:`n$err" }
    return (@($out) -join "`n")   # one string, so ConvertFrom-Json sees the whole document
}
function New-RandomSecret([int] $bytes) {
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $buf = New-Object byte[] $bytes; $rng.GetBytes($buf)
    return ([Convert]::ToBase64String($buf) -replace '[/+=]', '')
}

# ---- 0. preflight ----
$account = Invoke-Az @("account", "show", "-o", "json") | ConvertFrom-Json
Write-Host "Subscription: $($account.name) ($($account.id)) as $($account.user.name)"
$sub = $account.id
$tenant = $account.tenantId
& gh auth status 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw "gh is not logged in (gh auth login)" }

# ---- 1. resource group + budget ----
Invoke-Az @("group", "create", "-n", $ResourceGroup, "-l", $Location, "--tags", "project=relayhub", "-o", "none") | Out-Null
Write-Host "Resource group $ResourceGroup ready in $Location"
Invoke-Az @("deployment", "sub", "create", "-l", $Location, "-n", "relayhub-budget-$Location", "-f", "infra/budget.bicep",
            "-p", "contactEmail=$BudgetEmail", "amount=$BudgetAmount", "-o", "none") | Out-Null
Write-Host "Budget alert: $BudgetAmount/month -> $BudgetEmail"

# ---- 2. secrets (generated once; afterwards they live in Key Vault and GitHub secrets only) ----
$sqlPassword = $env:SQL_ADMIN_PASSWORD
if (-not $sqlPassword) { $sqlPassword = (New-RandomSecret 24) + "Aa1!" }
$masterKey = $env:API_KEY_MASTER_KEY
if (-not $masterKey) { $masterKey = New-RandomSecret 48 }
# Week 3: dashboard auth. The JWT key is never shown; the owner password is the one thing Tim must know, so it is shown once when generated.
$jwtKey = $env:JWT_SIGNING_KEY
if (-not $jwtKey) { $jwtKey = New-RandomSecret 48 }
if (-not $OwnerEmail) { $OwnerEmail = $env:OWNER_EMAIL }
if (-not $OwnerEmail) { $OwnerEmail = $BudgetEmail }
$ownerPasswordGenerated = $false
if (-not $OwnerPassword) { $OwnerPassword = $env:OWNER_PASSWORD }
if (-not $OwnerPassword) { $OwnerPassword = New-RandomSecret 18; $ownerPasswordGenerated = $true }
$deployerId = Invoke-Az @("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv")

# ---- 3. infrastructure ----
$env:SQL_ADMIN_PASSWORD = $sqlPassword
$env:API_KEY_MASTER_KEY = $masterKey
$env:JWT_SIGNING_KEY = $jwtKey
$env:OWNER_EMAIL = $OwnerEmail
$env:OWNER_PASSWORD = $OwnerPassword
$env:DEPLOYER_OBJECT_ID = "$deployerId".Trim()
$env:SQL_LOCATION = $SqlLocation
$env:SIGNALR_LOCATION = $SignalRLocation
$env:STATIC_WEB_APP_LOCATION = $StaticWebAppLocation
Write-Host "Deploying infra/main.bicep (3-6 minutes; the SQL server takes longest)..."
$outputs = Invoke-Az @("deployment", "group", "create", "-g", $ResourceGroup, "-n", "relayhub-main", "-f", "infra/main.bicep",
                       "-p", "infra/main.bicepparam", "--query", "properties.outputs", "-o", "json") | ConvertFrom-Json
Write-Host "API URL:     $($outputs.apiUrl.value)"
Write-Host "Dashboard:   $($outputs.dashboardUrl.value)  (empty until deploy.yml uploads web/dist)"
Write-Host "SignalR:     $($outputs.signalRName.value) -> $($outputs.signalREndpoint.value)"
Write-Host "SQL server:  $($outputs.sqlServerFqdn.value) / $($outputs.sqlDatabaseName.value)"
Write-Host "Key Vault:   $($outputs.keyVaultName.value)"

# ---- 4. GitHub Actions identity (OIDC, no stored Azure secrets) ----
$appId = (Invoke-Az @("ad", "app", "list", "--display-name", $GitHubAppName, "--query", "[0].appId", "-o", "tsv") | Out-String).Trim()
if (-not $appId) { $appId = (Invoke-Az @("ad", "app", "create", "--display-name", $GitHubAppName, "--query", "appId", "-o", "tsv") | Out-String).Trim() }
# PS 5.1 turns native stderr into a terminating error under ErrorActionPreference=Stop; "not found" is expected here.
$spId = ""
try { $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"; $spId = (& az ad sp show --id $appId --query id -o tsv 2>$null | Out-String).Trim() } catch { $spId = "" } finally { $ErrorActionPreference = $prev }
if (-not $spId) { $spId = (Invoke-Az @("ad", "sp", "create", "--id", $appId, "--query", "id", "-o", "tsv") | Out-String).Trim() }
$scope = "/subscriptions/$sub/resourceGroups/$ResourceGroup"
foreach ($role in @("Contributor", "Role Based Access Control Administrator")) {
    Invoke-Az @("role", "assignment", "create", "--assignee-object-id", $spId, "--assignee-principal-type", "ServicePrincipal",
                "--role", $role, "--scope", $scope, "-o", "none") | Out-Null
}
# deploy.yml runs in the GitHub environment "production", so the OIDC subject is the environment form.
# GitHub now includes owner/repo ids in the subject: repo:<owner>@<ownerId>/<repo>@<repoId>:environment:production
$owner, $repoName = $Repo.Split("/")
$ownerId = (& gh api "users/$owner" --jq .id | Out-String).Trim()
$repoId = (& gh api "repos/$Repo" --jq .id | Out-String).Trim()
$subject = "repo:$owner@$ownerId/$repoName@$repoId`:environment:production"
$existing = (Invoke-Az @("ad", "app", "federated-credential", "list", "--id", $appId, "--query", "[?subject=='$subject'] | length(@)", "-o", "tsv") | Out-String).Trim()
if ($existing -eq "0") {
    $fc = @{ name = "github-production"; issuer = "https://token.actions.githubusercontent.com";
             subject = $subject; audiences = @("api://AzureADTokenExchange") } | ConvertTo-Json -Compress
    $tmp = Join-Path $env:TEMP "relayhub-fc.json"; Set-Content -Path $tmp -Value $fc -Encoding ascii
    Invoke-Az @("ad", "app", "federated-credential", "create", "--id", $appId, "--parameters", $tmp, "-o", "none") | Out-Null
    Remove-Item $tmp
}
Write-Host "GitHub deploy identity: $GitHubAppName ($appId), Contributor + RBAC Administrator on $ResourceGroup"

# ---- 5. GitHub variables + secrets for deploy.yml / keep-warm.yml ----
& gh variable set AZURE_CLIENT_ID --repo $Repo --body $appId
& gh variable set AZURE_TENANT_ID --repo $Repo --body $tenant
& gh variable set AZURE_SUBSCRIPTION_ID --repo $Repo --body $sub
& gh variable set API_URL --repo $Repo --body $outputs.apiUrl.value
& gh variable set DASHBOARD_URL --repo $Repo --body $outputs.dashboardUrl.value
$sqlPassword | & gh secret set SQL_ADMIN_PASSWORD --repo $Repo
$masterKey | & gh secret set API_KEY_MASTER_KEY --repo $Repo
$jwtKey | & gh secret set JWT_SIGNING_KEY --repo $Repo
$OwnerEmail | & gh secret set OWNER_EMAIL --repo $Repo
$OwnerPassword | & gh secret set OWNER_PASSWORD --repo $Repo
# Optional channel secrets: only when present in the environment (the deployment already used the same values).
foreach ($name in @("OPS_DISCORD_WEBHOOK_URL", "TURNSTILE_SECRET", "META_APP_SECRET", "META_VERIFY_TOKEN", "TELEGRAM_WEBHOOK_SECRET", "EMAIL_INBOUND_SECRET", "RESEND_API_KEY")) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value) { $value | & gh secret set $name --repo $Repo; Write-Host "GitHub secret $name set" }
}
if ($env:RESEND_FROM) { & gh variable set RESEND_FROM --repo $Repo --body $env:RESEND_FROM }
# The Static Web App deployment token is NOT stored anywhere: deploy.yml reads it with `az staticwebapp secrets list` on every run.
Write-Host "GitHub variables and secrets set. Push to main (or run the 'Deploy API' workflow) to deploy."
if ($ownerPasswordGenerated) {
    Write-Host ""
    Write-Host "Dashboard owner login: $OwnerEmail"
    Write-Host "Dashboard owner password (generated, shown ONCE; also in Key Vault secret RelayHub--OwnerPassword and GitHub secret OWNER_PASSWORD):"
    Write-Host "  $OwnerPassword"
    Write-Host ""
}
Write-Host "Done. Secrets live in Key Vault $($outputs.keyVaultName.value) and GitHub repo secrets; nothing else was printed."
