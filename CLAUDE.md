# RelayHub — notes for Claude Code (and humans)

Read `docs/HANDOFF.md` first: it says what is done, what is next and what is blocked.

## What this is

Tim Tran's portfolio project for Calgary .NET / full-stack roles. A **unified customer inbox for small businesses** (Instagram, Facebook Page, website form, email, Telegram in one dashboard, reply from the same screen) built on a **reliable integration hub** (signed webhooks, queue-first ingest, retries with backoff, dead-letter, replay, tracing). The pitch leads with the inbox; the engine is what impresses technical interviewers.

The plan is the source of truth: `docs/plan-src/plan.md` (readable) → `docs/RelayHub-Project-Plan-v2.docx` (generated, see `docs/plan-src/README.md`). Do not edit the docx by hand; edit `plan.md` and rebuild. `docs/architecture.png` is generated from `docs/plan-src/architecture.html`. `mockup/relayhub-mockup.html` is the clickable UI mockup (open it in a browser).

## Layout

```
src/RelayHub.Api             ASP.NET Core minimal APIs (.NET 10): JWT auth (Auth/), ingest (verify + enqueue only), inbox / conversations / reply / stats / realtime negotiate, replay, Scalar docs at /docs
src/RelayHub.Worker          Azure Functions isolated worker: ProcessIncoming (incoming queue), ProcessDelivery (deliveries queue), PoisonAlerts
src/RelayHub.Core            entities, enums, IDestinationConnector, IReplyChannel, IRealtimeNotifier, IRelayQueues + queue message records, ApiKeys (HMAC), ReplyWindow, ChannelNames, TemplateRenderer
src/RelayHub.Infrastructure  EF Core DbContext + migrations, StorageQueues, connectors (Discord), DeliveryDispatcher, IncomingProcessor, DeliveryProcessor, DiscordOpsAlerts, SignalRRealtimeNotifier
tests/RelayHub.UnitTests     xUnit
tests/RelayHub.IntegrationTests  WebApplicationFactory; Testcontainers (SQL Server) tests carry [Trait("Category","Docker")]
web/                         Vite + React + TypeScript dashboard (default scaffold until Week 3)
infra/                       Bicep: App Service F1 (API), Functions Flex Consumption (worker), SQL free offer, Storage queues, Key Vault, App Insights
scripts/smoke.sh             curl walk-through of the flow against a running API (+ Azurite + worker for the stored event)
```

## Commands

```bash
dotnet build RelayHub.slnx
dotnet test RelayHub.slnx --filter "Category!=Docker"   # what CI runs
dotnet test RelayHub.slnx                                # also the Docker/Testcontainers flow (needs Docker)
dotnet run --project src/RelayHub.Api --urls http://localhost:5080   # then BASE=http://localhost:5080 bash scripts/smoke.sh (logs in as the dev owner first)
cd src/RelayHub.Worker && func start                                 # worker against Azurite + local SQL (local.settings.json)
dotnet ef migrations add <Name> -p src/RelayHub.Infrastructure        # dotnet-ef is a local tool (dotnet-tools.json)
cd web && npm ci && npm run build
```

## Local setup (per machine)

1. .NET 10 SDK, Node 24, Docker.
2. Azurite (queues) in Docker: `docker run -d --name relayhub-azurite -p 10000-10002:10000-10002 --restart unless-stopped mcr.microsoft.com/azure-storage/azurite`. `appsettings.Development.json` and `local.settings.sample.json` already use `UseDevelopmentStorage=true`.
   SQL Server in Docker: `docker run -d --name relayhub-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong pw>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`. On Apple Silicon the image is amd64: enable Rosetta / "Use Rosetta for x86_64 emulation" in Docker Desktop, or use Colima (`colima start --vm-type vz --vz-rosetta`) and export `DOCKER_HOST=unix://$HOME/.colima/default/docker.sock` plus `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`. On macOS run the API on a port other than 5000 (AirPlay Receiver owns it): `--urls http://localhost:5080`.
3. Secrets live in user-secrets, never in the repo:
   ```bash
   cd src/RelayHub.Api
   dotnet user-secrets set "ConnectionStrings:RelayHub" "Server=localhost,1433;Database=RelayHub;User Id=sa;Password=<strong pw>;TrustServerCertificate=True"
   dotnet user-secrets set "RelayHub:ApiKeyMasterKey" "<long random string>"
   dotnet user-secrets set "RelayHub:MigrateOnStartup" "true"
   dotnet user-secrets set "RelayHub:SeedOnStartup" "true"
   dotnet user-secrets set "RelayHub:SeedDiscordWebhookUrl" "https://discord.com/api/webhooks/..."   # optional
   dotnet user-secrets set "RelayHub:JwtSigningKey" "<32+ random characters>"                        # optional in Development (built-in dev key + warning)
   dotnet user-secrets set "RelayHub:OwnerEmail" "you@example.com"                                   # optional in Development (owner@relayhub.local / relayhub-dev-owner)
   dotnet user-secrets set "RelayHub:OwnerPassword" "<password>"
   dotnet user-secrets set "SignalR:ConnectionString" "Endpoint=https://...;AccessKey=...;Version=1.0;"  # optional: live dashboard updates
   ```
   After `user-secrets init` or any csproj change, rebuild before `dotnet run --no-build` (the secrets id is compiled in).
4. The seeder logs the `website-contact` API key on startup. Keys are derived from the master key, never stored.
5. Worker: `cp src/RelayHub.Worker/local.settings.sample.json src/RelayHub.Worker/local.settings.json` and put the same SQL connection string in it (gitignored). `func` = Azure Functions Core Tools v4 (`npm i -g azure-functions-core-tools@4` works on macOS when the Homebrew tap is refused).

## Decisions that must not be undone (see plan sections 5.2 and 4.3)

- Queue-first ingest: webhook endpoints verify the signature and enqueue; they never touch SQL. The worker resolves the source from the key id (so revocation and the source-name check happen at processing time), stores the event, creates Pending deliveries and queues one `DeliveryMessage` per delivery.
- One queue message = one delivery attempt. Connector failures are handled in `DeliveryProcessor` (re-enqueue with `DeliveryDispatcher.DelayAfter` as the visibility delay, dead-letter after 5, alert); only crashes throw, so the Functions host retries (`maxDequeueCount` 5) and parks the message on `{queue}-poison`, which `PoisonAlerts` reports.
- At-least-once, made safe: events dedupe on (SourceId, ExternalId); a `DeliveryMessage` carries `AfterAttempt` and is skipped when the delivery's AttemptCount moved on (duplicate or stale retry after a replay). Accepted trade-off: a destination may very rarely see the same notification twice.
- Replay (`POST /api/v1/deliveries/{id}/replay`) keeps the attempt history and gives exactly one more attempt; it is also the recovery for a delivery stuck in Retrying (worker crashed between SaveChanges and re-enqueue). Nothing polls SQL for stuck deliveries on purpose.
- Never poll Azure SQL (serverless free offer must sleep). `/health` never touches the DB; `/health/ready` does.
- Every query is workspace-scoped through EF Core global query filters (`ICurrentWorkspace`). Do not remove the filters.
- Auth (Week 3): HS256 JWT bearer (`RelayHub:JwtSigningKey`, ≥ 32 chars), claims `sub email role ws wsn demo`, Owner 7 days / Demo 2 hours. The ambient workspace comes ONLY from the token's `ws` claim (`UseWorkspaceFromToken` after `UseAuthentication`); the `X-Workspace-Id` header is gone and must not come back. Unauthenticated requests have no workspace (filters match nothing). Policies: `Owner` (role Owner) for configuration writes, `Member` (Owner or Demo) for everything else; hooks, `/auth/login`, `/auth/demo`, `/health*`, `/docs`, `/openapi/*` stay anonymous. Users table holds only `PasswordHasher<User>` hashes (no Identity framework); the owner comes from `RelayHub:OwnerEmail` / `OwnerPassword` through the seeder (rotating the secret re-hashes on the next startup). Demo tokens are mocked actions on the `IsDemo` workspace: `reply` → `SentDemo`, `destinations/{id}/test` → mocked, no configuration writes.
- Realtime (Week 3): Azure SignalR Service in **serverless** mode through the Management SDK (`SignalRRealtimeNotifier`, hub `relayhub`, group `ws-{workspaceId}`, messages `eventCreated` / `deliveryUpdated` / `replySent`), configured by `SignalR:Endpoint` (Managed Identity, role SignalR Service Owner) or `SignalR:ConnectionString`; unset → `NullRealtimeNotifier` and `POST /api/v1/realtime/negotiate` answers 503 `realtime-unavailable`. Both the API and the worker push through the same class; it never throws. The dashboard must never poll on a timer (serverless SQL must sleep): live updates or a manual Refresh button.
- Source API keys are stateless signed keys `src_{keyId}_{hmac}`; verify with constant-time compare; the key id finds the source.
- Replies are synchronous (user is waiting), notification deliveries are asynchronous with backoff 30 s / 2 min / 10 min / 30 min / 1 h, dead-letter after 5 attempts. `DeliveryDispatcher` owns that rule; reuse it from the worker.
- Discord / Telegram / email are "borrowed" push notifications; there is no mobile app.
- Meta channels: the app stays in development mode (only role holders can message it). The public demo uses simulators, never live Meta. Real-usage metrics come from the website form, email and Telegram.
- Resume and README metrics must be real numbers. Keep a build journal of real bugs in `docs/` for interview stories.
- Secrets and webhook URLs never go into the repo or into API responses (configs are masked). Azure: Key Vault references + system-assigned managed identities for the API and the worker; the worker's host storage and both apps' queue access are identity-based (`AzureWebJobsStorage__accountName`, `Storage:QueueServiceUri` + DefaultAzureCredential). SQL still uses SQL auth through Key Vault (Entra-only auth is a later step).
- Trace context travels in the queue message (`TraceParent`); each worker function starts an Activity with that parent. OpenTelemetry → Application Insights in both apps (API: Azure Monitor distro; worker: Functions OpenTelemetry + Azure Monitor exporter), enabled only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set.

## Conventions

- C# 14 / .NET 10, nullable + implicit usings on, minimal APIs grouped per feature in `src/RelayHub.Api/Endpoints`, Problem Details for errors, `Results.ValidationProblem` for 400s.
- Entity named `Delivery` ⇒ do not create a namespace called `...Delivery` in Infrastructure (it clashes); the dispatcher lives in `RelayHub.Infrastructure.Dispatch`.
- Commit messages: imperative summary + short bullet body. CI (`.github/workflows/ci.yml`) must stay green. Every push to `main` touching `src/**` or `infra/**` deploys to Azure (`deploy.yml`); `gh run watch` it. `gh workflow run diagnose.yml` prints the live worker / queue / Key Vault / App Insights status when there is no local `az login`.
- Tim prefers chat replies in Vietnamese (casual); code, comments and docs stay in English.
- Interview prep: when Tim asks to practise, follow the "Interview prep routine" in `docs/HANDOFF.md` (mock interview in English on the decisions above, follow-up questions, model answers, build-journal stories).
