# Infrastructure (Bicep)

Everything runs on free tiers (plan section 10). `infra/main.bicep` creates, in resource group `rg-relayhub`:

| Resource | Name (live) | Tier / limits | Purpose |
| --- | --- | --- | --- |
| App Service plan + web app | `asp-relayhub-prod`, `app-relayhub-prod-api-s4w2qp` | F1 (60 CPU-min/day, no Always On) | the API |
| Functions Flex Consumption | `asp-relayhub-prod-worker`, `func-relayhub-prod-worker-s4w2qp` | free grant 250k executions + 100k GB-s/month | the queue worker |
| Azure SQL | `sql-relayhub-prod-s4w2qp` / `relayhub` | free offer, serverless, auto-pause | data |
| Storage account | `strelayhubs4w2qp` | Standard_LRS | queues `incoming`, `deliveries`, `*-poison`; worker package container |
| Key Vault | `kv-relayhub-s4w2qp` | standard, RBAC | every secret; apps read them through Key Vault references |
| Log Analytics + App Insights | `log-relayhub-prod-s4w2qp`, `appi-relayhub-prod` | 0.1 GB/day cap, 5 GB/month free | telemetry |
| **Azure SignalR Service** (Week 3) | `sigr-relayhub-prod-s4w2qp` | **Free_F1**: 1 unit, 20 concurrent connections, 20 000 messages/day | realtime push to the dashboard, **Serverless** mode, identity-based |
| **Static Web App** (Week 3) | `stapp-relayhub-prod` | **Free**: 100 GB bandwidth/month, 0.25 GB app, free TLS, global edge | the React dashboard (`web/dist`) |

Regions: API in `canadaeast`; SQL, worker and SignalR in `canadacentral` (parameters `sqlLocation`, `functionsLocation`, `signalRLocation`); the Static Web App resource in `eastus2` (`staticWebAppLocation`) because Static Web Apps is only offered in westus2, centralus, eastus2, westeurope and eastasia — its content is served from a global edge anyway. The SignalR Free tier is enough for the demo (one dashboard tab = one connection); upgrading to Standard_S1 is a one-line SKU change.

## One-time bootstrap

Windows: `.\infra\bootstrap.ps1 -BudgetEmail <you> [-OwnerEmail <login>] [-OwnerPassword '<pw>']` does everything below (idempotent, but see the rotation note in its header). Manual steps, from any machine signed in with `az login` and `gh auth login`:

```bash
az account show --query '{name:name,id:id}'                      # confirm the right subscription
LOCATION=canadaeast      # F1 quota was 0 in canadacentral on the free trial; canadaeast works
az group create -n rg-relayhub -l $LOCATION --tags project=relayhub

# budget alert first — it is the safety net
az deployment sub create -l $LOCATION -f infra/budget.bicep -p contactEmail=<your email> amount=5

# secrets: generate once, keep them in Key Vault only
export SQL_ADMIN_PASSWORD="$(openssl rand -base64 24 | tr -d '/+=' )Aa1!"
export API_KEY_MASTER_KEY="$(openssl rand -base64 48 | tr -d '/+=')"
export JWT_SIGNING_KEY="$(openssl rand -base64 48)"                 # Week 3: signs the dashboard JWTs (>= 32 chars)
export OWNER_EMAIL="you@example.com"                                 # Week 3: dashboard owner login, created by the seeder
export OWNER_PASSWORD="<a password you will remember>"
export DEPLOYER_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)"
az deployment group create -g rg-relayhub -f infra/main.bicep -p infra/main.bicepparam
```

`infra/main.bicepparam` reads every secret from the environment (`readEnvironmentVariable`). The three Week 3 variables have **no default**: when one is missing the Bicep compile stops with `Environment variable "JWT_SIGNING_KEY" does not exist` before anything is deployed. Optional variables default to empty, which means "do not create that secret and do not emit its app setting".

Outputs include the API URL, the dashboard URL (`https://<random>.azurestaticapps.net`), the SignalR name/endpoint, the SQL server FQDN and the Key Vault name.

## Secrets and settings (Key Vault secret `A--B` ↔ app setting `A__B` ↔ configuration `A:B`)

| GitHub secret / variable | Key Vault secret | Goes to | Required? |
| --- | --- | --- | --- |
| `SQL_ADMIN_PASSWORD` | `ConnectionStrings--RelayHub` (built) | API, worker | yes |
| `API_KEY_MASTER_KEY` | `RelayHub--ApiKeyMasterKey` | API | yes |
| `JWT_SIGNING_KEY` | `RelayHub--JwtSigningKey` | API | **yes** (Week 3) |
| `OWNER_EMAIL` | `RelayHub--OwnerEmail` | API | **yes** (Week 3) |
| `OWNER_PASSWORD` | `RelayHub--OwnerPassword` | API | **yes** (Week 3) |
| `OPS_DISCORD_WEBHOOK_URL` | `RelayHub--OpsDiscordWebhookUrl` | API, worker | optional |
| `TURNSTILE_SECRET` | `RelayHub--Turnstile--Secret` | API | optional (demo login bot check) |
| `META_APP_SECRET` | `RelayHub--Meta--AppSecret` | API | optional (Facebook / Instagram hooks) |
| `META_VERIFY_TOKEN` | `RelayHub--Meta--VerifyToken` | API | optional |
| `TELEGRAM_WEBHOOK_SECRET` | `RelayHub--Telegram--WebhookSecret` | API | optional |
| `EMAIL_INBOUND_SECRET` | `RelayHub--Email--InboundSecret` | API | optional (Cloudflare Email Worker) |
| `RESEND_API_KEY` | `RelayHub--Resend--ApiKey` | API, worker | optional (outbound email) |
| variable `RESEND_FROM` | — (plain app setting `RelayHub__Resend__From`) | API, worker | optional |

Settings that are not secrets and need no GitHub input: `RelayHub__DashboardOrigin` = `https://<swa default hostname>` (CORS), `SignalR__Endpoint` = `https://sigr-….service.signalr.net` (both apps; identity-based, no connection string), `RelayHub__SeedOnStartup = true` (the seeder creates/updates the owner user from the two owner secrets on every start).

```bash
gh secret set JWT_SIGNING_KEY --body "$(openssl rand -base64 48)"
gh secret set OWNER_EMAIL --body "you@example.com"
gh secret set OWNER_PASSWORD            # prompts; or --body '<pw>'
# optional, whenever a channel gets wired up:
gh secret set TURNSTILE_SECRET; gh secret set META_APP_SECRET; gh secret set META_VERIFY_TOKEN
gh secret set TELEGRAM_WEBHOOK_SECRET; gh secret set EMAIL_INBOUND_SECRET; gh secret set RESEND_API_KEY
gh variable set RESEND_FROM --body "RelayHub <inbox@example.com>"
# keep-warm.yml (optional; defaults to the live API):
gh variable set API_URL --body https://app-relayhub-prod-api-s4w2qp.azurewebsites.net
gh variable set DASHBOARD_URL --body https://<swa hostname>
```

Rule baked into the Bicep (`opsWebhookSettings`, `optionalSecretSettings`): an app setting with a Key Vault reference is only emitted when the secret is actually deployed. A reference to a missing secret is handed to the app as the literal `@Microsoft.KeyVault(...)` string, which is far worse than an absent setting (the feature then answers 503 "not configured" and logs one warning).

Rotation: change the GitHub secret and push (or run Deploy API). The Bicep writes the new value to Key Vault; App Service re-resolves references on restart, which the package deploy triggers. A new `JWT_SIGNING_KEY` logs everyone out; a new `OWNER_PASSWORD` is re-hashed by the seeder; a new `API_KEY_MASTER_KEY` invalidates every source API key.

## Realtime: Azure SignalR Service (serverless)

- Serverless mode (`features: ServiceMode = Serverless`): there is no hub server. The API serves `POST /api/v1/realtime/negotiate`; the API and the worker push through the Management SDK (REST). Browsers then connect to the service directly, so the resource's CORS allows exactly one origin: the dashboard.
- Both managed identities get **SignalR Service Owner** (`7e4f1700-ea5a-4f59-8f37-079cfe29dce3`, data action `Microsoft.SignalRService/SignalR/*`) on the resource. The SignalR managed-identity guide names this role for "a negotiation server with an Azure SignalR Service resource running in Serverless mode"; the narrower *SignalR REST API Owner* has no `auth/accessKey/action` and *SignalR App Server* has no `hub/*`, so neither alone covers negotiate + send. Role ids are copied from https://learn.microsoft.com/azure/role-based-access-control/built-in-roles/web-and-mobile — never typed from memory (build journal, 2026-10-13). New role assignments can take up to 30 minutes to propagate: a fresh deploy may answer 503 `realtime-unavailable` / 403 from the service for a while.
- Access keys stay enabled (`disableLocalAuth` default) so a developer can use `SignalR:ConnectionString` locally; nothing in Azure uses them. Flip `disableLocalAuth: true` once local dev uses its own resource or the emulator.

## Dashboard: Static Web App

- No repository binding (`provider: Custom`). `deploy.yml` builds `web/` with `VITE_API_BASE_URL=<apiUrl>`, reads the deployment token with `az staticwebapp secrets list -n stapp-relayhub-prod -g rg-relayhub --query properties.apiKey -o tsv` (masked in the log), and uploads `web/dist` with `Azure/static-web-apps-deploy@v1` (`skip_app_build: true`). There is **no** `AZURE_STATIC_WEB_APPS_API_TOKEN` secret to manage or rotate; `az staticwebapp secrets reset-api-key` is harmless.
- The dashboard is a SPA: `web/public/staticwebapp.config.json` **must exist** (owned by the dashboard code) with `navigationFallback` → `/index.html` (excluding `/assets/*`), otherwise deep links such as `/app/inbox` return 404. Vite copies `public/` into `dist/`; the deploy prints a warning when the file is missing from `dist/`.
- The API's CORS origin (`RelayHub__DashboardOrigin`) and the SignalR CORS list are both derived from the SWA's `defaultHostname` in Bicep, so a custom domain later means adding it in one place (`main.bicep`) and redeploying.

## GitHub Actions deploy (OIDC, no stored Azure credentials)

```bash
SUB=$(az account show --query id -o tsv)
APP_ID=$(az ad app create --display-name relayhub-github-deploy --query appId -o tsv)
az ad sp create --id $APP_ID >/dev/null
SP_ID=$(az ad sp show --id $APP_ID --query id -o tsv)
az role assignment create --assignee-object-id $SP_ID --assignee-principal-type ServicePrincipal \
  --role Contributor --scope /subscriptions/$SUB/resourceGroups/rg-relayhub
az role assignment create --assignee-object-id $SP_ID --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope /subscriptions/$SUB/resourceGroups/rg-relayhub
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-main", "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:thongminhtran@<ownerId>/relay-hub@<repoId>:environment:production", "audiences": ["api://AzureADTokenExchange"] }'
# (deploy.yml uses the GitHub environment "production"; ids via: gh api users/thongminhtran --jq .id ; gh api repos/thongminhtran/relay-hub --jq .id)

gh variable set AZURE_CLIENT_ID --body $APP_ID
gh variable set AZURE_TENANT_ID --body $(az account show --query tenantId -o tsv)
gh variable set AZURE_SUBSCRIPTION_ID --body $SUB
```

The `deploy.yml` workflow (push to `main` touching `src/**`, `web/**`, `infra/**`) then: OIDC login → `az deployment group create` (idempotent) → publish API + worker → EF Core migration bundle through a temporary SQL firewall rule → worker to Flex Consumption → API package → `/health` smoke → worker functions registered → **build `web/` → upload to the Static Web App → dashboard `200` smoke → `POST /api/v1/auth/demo` (200 or 503 expected; anything else is a warning, not a failure)**.

Other workflows: `ci.yml` (fast .NET job without Docker tests, a second job running the whole suite incl. Testcontainers, dashboard lint/test/build), `keep-warm.yml` (every 10 minutes, roughly 07:00–23:00 Calgary, `GET /health` — never `/health/ready`, the SQL must sleep — plus one `GET` on the dashboard when `DASHBOARD_URL` is set), `diagnose.yml` (manual: live worker / queues / Key Vault references / SignalR / SWA / app setting names / App Insights without a local `az login`).

## Cost guardrails baked in

- SQL: `useFreeLimit` + `freeLimitExhaustionBehavior: AutoPause` — can never bill. Auto-pause after 60 minutes idle; nothing polls it (keep-warm hits `/health` only).
- Log Analytics daily cap 0.1 GB; App Insights stays inside the free 5 GB/month.
- F1 plan, Flex Consumption free grant, Standard_LRS storage, Key Vault standard, SignalR Free_F1, Static Web App Free: $0 to a few cents.
- Budget alert at $5 with emails at 50 / 80 / 100 %.
- keep-warm runs ~100 times a day: free on a public repository; on a private one each run bills about one Actions minute (≈ 3 000 minutes/month, above the 2 000 free), so narrow the cron or make the repo public.
