# RelayHub

**Unified customer inbox for small businesses, built on a reliable integration hub.**

Messages from Instagram, a Facebook Page, the website contact form, email and Telegram arrive in one web dashboard, and you reply from that same screen. Under the hood: a queue-first event pipeline on Azure with signed webhooks, retries with exponential backoff, dead-lettering, one-click replay and end-to-end tracing. Nothing gets lost.

> Status: **Week 1 of 6** — core API, data model and the first connector (Discord). See the [project plan](docs/RelayHub-Project-Plan-v2.docx) and the [architecture diagram](docs/architecture.png).

![RelayHub architecture](docs/architecture.png)

## Stack

C# / .NET 10 · ASP.NET Core minimal APIs · EF Core 10 · Azure Functions (isolated) · Azure SQL (free offer) · Azure Storage Queues · Azure SignalR · React + TypeScript (Vite) · GitHub Actions · Bicep

## Repository layout

```
src/RelayHub.Api             ASP.NET Core Web API: webhook ingest, REST API, OpenAPI docs (/docs)
src/RelayHub.Worker          Azure Functions worker: queue-triggered processing, retries, dead-letter
src/RelayHub.Core            domain model, routing, templates, connector interfaces
src/RelayHub.Infrastructure  EF Core DbContext + migrations, connectors (Discord, ...)
web/                         React + TypeScript dashboard
tests/                       xUnit unit tests; integration tests (WebApplicationFactory, Testcontainers)
infra/                       Bicep templates (Week 5)
docs/                        plan, architecture diagram, clickable mockup
```

## Run locally

1. SQL Server (any edition). Easiest: `docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<strong password>" -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest`
2. Configure secrets (never commit them):
   ```powershell
   cd src/RelayHub.Api
   dotnet user-secrets set "ConnectionStrings:RelayHub" "Server=localhost,1433;Database=RelayHub;User Id=sa;Password=<strong password>;TrustServerCertificate=True"
   dotnet user-secrets set "RelayHub:ApiKeyMasterKey" "<long random string>"
   dotnet user-secrets set "RelayHub:MigrateOnStartup" "true"
   dotnet user-secrets set "RelayHub:SeedOnStartup" "true"
   dotnet user-secrets set "RelayHub:SeedDiscordWebhookUrl" "https://discord.com/api/webhooks/..."   # optional
   ```
3. `dotnet run --project src/RelayHub.Api` — the console prints the API key for the `website-contact` source. Docs at `https://localhost:<port>/docs`.
4. Send a test message:
   ```http
   POST /api/v1/hooks/website-contact
   X-Api-Key: src_...
   Idempotency-Key: 4b1e9a7c-2d6f-4f0e-9a51-1
   Content-Type: application/json

   { "name": "Anna Nguyen", "email": "anna@example.com", "message": "Do you build websites for restaurants?" }
   ```
   Response `202 Accepted` with a correlation id; the message is stored and (if a Discord route exists) posted to Discord. Sending it again with the same `Idempotency-Key` is ignored.
5. Configure through the API (all under `/docs`): `POST /api/v1/sources` returns the API key once, `POST /api/v1/destinations` (Discord: `{ "webhookUrl": "..." }`), `POST /api/v1/destinations/{id}/test`, `POST /api/v1/routes` with a `{{name}} {{text}} {{channel}}` template. `scripts/smoke.sh` runs the whole flow with curl.
6. Dashboard: `cd web && npm install && npm run dev` (Week 3).

Tests: `dotnet test` (unit + in-process API). `dotnet test --filter Category=Docker` additionally runs the end-to-end flow against SQL Server in Docker via Testcontainers. Migrations: `dotnet ef migrations add <Name> -p src/RelayHub.Infrastructure`.

## Roadmap

| Week | Deliverable |
|---|---|
| 1 | Core API, EF Core + SQL, generic ingest, Discord connector, OpenAPI, deploy |
| 2 | Storage Queues + Functions worker, retries / backoff, dead-letter, replay, Key Vault, tracing |
| 3 | React dashboard: inbox, conversations, event timeline, stats, SignalR live updates |
| 4 | Facebook Page + Instagram (inbound + reply), Telegram, inbound email, Teams + email connectors |
| 5 | Public demo workspace with simulators, tests, Bicep, CI on every PR |
| 6 | README demo GIF, real usage for the studio, resume |
