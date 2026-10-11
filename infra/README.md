# Infrastructure (Bicep)

Everything runs on free tiers (plan section 10): App Service F1 (API), Azure SQL free offer (serverless, auto-pause), Storage account + queues, Key Vault (RBAC), Log Analytics + Application Insights (0.1 GB/day cap). Functions Flex Consumption and Static Web Apps are added in Weeks 2 and 3.

## One-time bootstrap

Windows: `.\infra\bootstrap.ps1 -BudgetEmail <you>` does everything below (idempotent). Manual steps, from any machine signed in with `az login` and `gh auth login`:

```bash
az account show --query '{name:name,id:id}'                      # confirm the right subscription
LOCATION=canadaeast      # F1 quota was 0 in canadacentral on the free trial; canadaeast works
az group create -n rg-relayhub -l $LOCATION --tags project=relayhub

# budget alert first — it is the safety net
az deployment sub create -l $LOCATION -f infra/budget.bicep -p contactEmail=<your email> amount=5

# secrets: generate once, keep them in Key Vault only
export SQL_ADMIN_PASSWORD="$(openssl rand -base64 24 | tr -d '/+=' )Aa1!"
export API_KEY_MASTER_KEY="$(openssl rand -base64 48 | tr -d '/+=')"
export DEPLOYER_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)"
az deployment group create -g rg-relayhub -f infra/main.bicep -p infra/main.bicepparam
```

Outputs include the API URL (`https://app-relayhub-prod-api.azurewebsites.net`), the SQL server FQDN and the Key Vault name.

## GitHub Actions deploy (OIDC, no stored secrets)

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
  "subject": "repo:thongminhtran/relay-hub:ref:refs/heads/main", "audiences": ["api://AzureADTokenExchange"] }'

gh variable set AZURE_CLIENT_ID --body $APP_ID
gh variable set AZURE_TENANT_ID --body $(az account show --query tenantId -o tsv)
gh variable set AZURE_SUBSCRIPTION_ID --body $SUB
```

The `deploy.yml` workflow then: logs in with OIDC → `az deployment group create` (idempotent) → publishes the API → runs the EF Core migration bundle against Azure SQL through a temporary firewall rule for the runner IP → deploys the package to App Service → smoke-checks `/health`.

## Cost guardrails baked in

- SQL: `useFreeLimit` + `freeLimitExhaustionBehavior: AutoPause` — can never bill. Auto-pause after 60 minutes idle.
- Log Analytics daily cap 0.1 GB; App Insights stays inside the free 5 GB/month.
- F1 plan, Standard_LRS storage, Key Vault standard: $0 to a few cents.
- Budget alert at $5 with emails at 50 / 80 / 100 %.
