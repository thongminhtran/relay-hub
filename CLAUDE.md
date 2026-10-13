# RelayHub — notes for Claude Code (and humans)

Read `docs/HANDOFF.md` first: it says what is done, what is next and what is blocked.

## What this is

Tim Tran's portfolio project for Calgary .NET / full-stack roles. A **unified customer inbox for small businesses** (Instagram, Facebook Page, website form, email, Telegram in one dashboard, reply from the same screen) built on a **reliable integration hub** (signed webhooks, queue-first ingest, retries with backoff, dead-letter, replay, tracing). The pitch leads with the inbox; the engine is what impresses technical interviewers.

The plan is the source of truth: `docs/plan-src/plan.md` (readable) → `docs/RelayHub-Project-Plan-v2.docx` (generated, see `docs/plan-src/README.md`). Do not edit the docx by hand; edit `plan.md` and rebuild. `docs/architecture.png` is generated from `docs/plan-src/architecture.html`. `mockup/relayhub-mockup.html` is the clickable UI mockup (open it in a browser).

## Layout

```
src/RelayHub.Api             ASP.NET Core minimal APIs (.NET 10): ingest, REST API, Scalar docs at /docs
src/RelayHub.Worker          Azure Functions isolated worker (queue triggers) — stub until Week 2
src/RelayHub.Core            entities, enums, IDestinationConnector, ApiKeys (HMAC), TemplateRenderer
src/RelayHub.Infrastructure  EF Core DbContext + migrations, connectors (Discord), DeliveryDispatcher
tests/RelayHub.UnitTests     xUnit
tests/RelayHub.IntegrationTests  WebApplicationFactory; Testcontainers (SQL Server) tests carry [Trait("Category","Docker")]
web/                         Vite + React + TypeScript dashboard (default scaffold until Week 3)
infra/                       Bicep (Week 5)
scripts/smoke.sh             curl walk-through of the Week 1 flow against a running API
```

## Commands

```bash
dotnet build RelayHub.slnx
dotnet test RelayHub.slnx --filter "Category!=Docker"   # what CI runs
dotnet test RelayHub.slnx                                # also the Docker/Testcontainers flow (needs Docker)
dotnet run --project src/RelayHub.Api --urls http://localhost:5000   # then BASE=http://localhost:5000 bash scripts/smoke.sh
dotnet ef migrations add <Name> -p src/RelayHub.Infrastructure        # dotnet-ef is a local tool (dotnet-tools.json)
cd web && npm ci && npm run build
```

## Local setup (per machine)

1. .NET 10 SDK, Node 24, Docker.
2. SQL Server in Docker: `docker run -d --name relayhub-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong pw>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`. On Apple Silicon the image is amd64: enable Rosetta / "Use Rosetta for x86_64 emulation" in Docker Desktop, or use Colima (`colima start --vm-type vz --vz-rosetta`) and export `DOCKER_HOST=unix://$HOME/.colima/default/docker.sock` plus `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`. On macOS run the API on a port other than 5000 (AirPlay Receiver owns it): `--urls http://localhost:5080`.
3. Secrets live in user-secrets, never in the repo:
   ```bash
   cd src/RelayHub.Api
   dotnet user-secrets set "ConnectionStrings:RelayHub" "Server=localhost,1433;Database=RelayHub;User Id=sa;Password=<strong pw>;TrustServerCertificate=True"
   dotnet user-secrets set "RelayHub:ApiKeyMasterKey" "<long random string>"
   dotnet user-secrets set "RelayHub:MigrateOnStartup" "true"
   dotnet user-secrets set "RelayHub:SeedOnStartup" "true"
   dotnet user-secrets set "RelayHub:SeedDiscordWebhookUrl" "https://discord.com/api/webhooks/..."   # optional
   ```
   After `user-secrets init` or any csproj change, rebuild before `dotnet run --no-build` (the secrets id is compiled in).
4. The seeder logs the `website-contact` API key on startup. Keys are derived from the master key, never stored.

## Decisions that must not be undone (see plan sections 5.2 and 4.3)

- Queue-first ingest: webhook endpoints verify the signature and enqueue; never touch SQL on the hot path (Week 2 moves delivery behind Storage Queues; Week 1 delivers inline on purpose).
- Never poll Azure SQL (serverless free offer must sleep). `/health` never touches the DB; `/health/ready` does.
- Every query is workspace-scoped through EF Core global query filters (`ICurrentWorkspace`). Until JWT auth (Week 3) the workspace comes from the `X-Workspace-Id` header, default 1 = Studio. Do not remove the filters.
- Source API keys are stateless signed keys `src_{keyId}_{hmac}`; verify with constant-time compare; the key id finds the source.
- Replies are synchronous (user is waiting), notification deliveries are asynchronous with backoff 30 s / 2 min / 10 min / 30 min / 1 h, dead-letter after 5 attempts. `DeliveryDispatcher` owns that rule; reuse it from the worker.
- Discord / Telegram / email are "borrowed" push notifications; there is no mobile app.
- Meta channels: the app stays in development mode (only role holders can message it). The public demo uses simulators, never live Meta. Real-usage metrics come from the website form, email and Telegram.
- Resume and README metrics must be real numbers. Keep a build journal of real bugs in `docs/` for interview stories.
- Secrets and webhook URLs never go into the repo or into API responses (configs are masked).

## Conventions

- C# 14 / .NET 10, nullable + implicit usings on, minimal APIs grouped per feature in `src/RelayHub.Api/Endpoints`, Problem Details for errors, `Results.ValidationProblem` for 400s.
- Entity named `Delivery` ⇒ do not create a namespace called `...Delivery` in Infrastructure (it clashes); the dispatcher lives in `RelayHub.Infrastructure.Dispatch`.
- Commit messages: imperative summary + short bullet body. CI (`.github/workflows/ci.yml`) must stay green.
- Tim prefers chat replies in Vietnamese (casual); code, comments and docs stay in English.
- Interview prep: when Tim asks to practise, follow the "Interview prep routine" in `docs/HANDOFF.md` (mock interview in English on the decisions above, follow-up questions, model answers, build-journal stories).
