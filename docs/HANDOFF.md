# RelayHub — hand-off / status

Last updated: 2026-10-11 (evening, Calgary). Keep this file current at the end of every working session.

## Where we are

Week 1 of the 6-week plan (`docs/RelayHub-Project-Plan-v2.docx`, source `docs/plan-src/plan.md`) is nearly done.

Done:
- Repo, .NET 10 solution, Vite React TS scaffold, GitHub Actions CI (green).
- Data model + EF Core migration `InitialCreate` (workspaces, sources, conversations, destinations, routes, events, deliveries, attempts).
- Generic ingest `POST /api/v1/hooks/{sourceKey}` with `X-Api-Key` (signed, stateless) and `Idempotency-Key` (unique index = dedupe) → stores Event + Conversation → routes → Discord connector (inline delivery for now).
- CRUD: sources (key returned once, rotate-key), destinations (Discord only, config validated and masked, `POST /{id}/test`), routes (templates with `{{name}} {{email}} {{text}} {{channel}} {{source}} {{receivedAt}}`).
- Inbox read endpoints: `GET /api/v1/events`, `GET /api/v1/events/{id}` (deliveries + attempts).
- `DeliveryDispatcher` with the backoff / dead-letter rule, shared with the Week 2 worker.
- `/health` (no DB), `/health/ready` (DB), Scalar docs at `/docs`, correlation id on every response.
- Tests: 10 unit + 4 integration (2 of them run the full flow against SQL Server in Docker via Testcontainers). `scripts/smoke.sh` verified against a real local SQL Server.
- Plan v2 + regenerated architecture diagram; clickable mockup in `mockup/`.

Week 1 is **complete** (2026-10-11): the API is live at https://app-relayhub-prod-api-s4w2qp.azurewebsites.net (`/health`, `/docs`), deployed by `.github/workflows/deploy.yml` (OIDC login → Bicep → publish → EF migration bundle through a temporary SQL firewall rule → App Service → `/health` smoke). Every push to `main` that touches `src/**` or `infra/**` redeploys.

## Next steps, in order

1. (done) Azure account → deploy API. Still worth doing early: connect the studio contact form to the live API (create a source via `POST /api/v1/sources` on the live API, put the key in the studio site's server config), and a Discord destination + route, so real numbers start accumulating.
2. Week 2 — reliability: Storage Queues (`incoming`, `deliveries`, poison), `RelayHub.Worker` queue triggers, move delivery out of the API (API: verify + enqueue + 200 within ms), retries by re-enqueue with visibility delay using `DeliveryDispatcher.Backoff`, dead-letter, `POST /api/v1/deliveries/{id}/replay`, Managed Identity + Key Vault, OpenTelemetry → Application Insights with trace context carried in queue messages, self-alert to Discord on dead-letter.
3. Week 3 — dashboard (`web/`): login (JWT, roles Owner/Demo, replaces the temporary `X-Workspace-Id` header), inbox, conversations with reply box + 24-hour countdown, event detail timeline, stats, SignalR live updates, wake-up screen for cold starts. Add a conversation/reply screen to `mockup/relayhub-mockup.html` first (one evening).
4. Week 4 — channels: Meta app in development mode (Facebook Page + Instagram inbound and reply through the Send API, 24-hour window enforced from `Conversation.LastInboundAt`), Telegram bot (in + out), inbound email via Cloudflare Email Worker, Teams + Resend email connectors. Free prep Tim can do any evening: create a Facebook Page for the studio, a studio Instagram account switched to Creator, the Meta developer app, a Telegram bot via @BotFather.
5. Week 5 — public demo workspace with signed simulators, mocked destinations and replies, failure switch (2/4/8 s backoff), Turnstile + rate limits, keep-warm GitHub Actions cron on `/health`, Bicep for everything, CI runs the Docker tests too.
6. Week 6 — README demo GIF, connect the studio's real channels, collect real metrics (form, email, Telegram), resume entry, rehearse the 5-minute demo (plan section 13).

## Decisions changed in plan v2 (2026-10-11)

- Pitch leads with "unified inbox" (think Pancake / Intercom / Meta Business Suite); the integration hub is the engine underneath.
- Reply from the dashboard is **in v1** (FB Page + Instagram via Meta Send API, synchronous).
- v1 channels: website form, Facebook Page, Instagram, inbound email, Telegram. WhatsApp, GitHub, Stripe → later.
- Meta development mode only lets app role holders message the Page / Instagram; App Review + business verification is an optional later milestone. The demo never depends on live Meta.
- Discord / Telegram / email are borrowed push notifications (no mobile app).
- Real users are a bonus from Tim's network, not a goal; resume metrics must be real.

## Interview prep routine (run this with Tim, in English)

The project only helps if Tim can defend every decision without looking at the code. Routine, about 30 minutes per session:

1. Tim reads one unit: the "Decisions" list in `CLAUDE.md`, plan section 5.2 (design decisions), section 14 (talking points), section 12 (skills map), or one entry of the build journal below.
2. The agent runs a **mock interview in English**: ask like a real Calgary interviewer, follow up on every answer ("why not Service Bus?", "what if SQL is paused?", "how do you stop tenant A seeing tenant B?", "walk me through one message end to end"), push back on vague answers, then give a short model answer and the one-line version for a resume conversation.
3. Finish with the 30-second pitch (plan section 13.1) said out loud, and one "hardest bug" story from the build journal.
4. Explanations of concepts Tim does not know yet: beginner level, prerequisites first, analogies, then the precise term. Code, docs and interview practice stay in English; casual chat can be Vietnamese.

Question bank to grow over time: `docs/interview-questions.md` (create it on first use; add every question Tim struggled with and the answer he finally gave).

## Azure subscription

- Free-trial subscription "Azure subscription 1" created 2026-10-11 on tminhthong1999@hotmail.com ($200 credit, spending limit on).
- **Upgrade to pay-as-you-go before 2026-11-10**, otherwise Azure disables the subscription and the live demo dies. Free tiers stay $0 after the upgrade; the $5 budget alert emails Tim.
- Resources live in resource group `rg-relayhub`, created by `infra/bootstrap.ps1` on 2026-10-11. Never create resources by hand in the portal; change `infra/main.bicep` and redeploy.
  - App Service F1 `app-relayhub-prod-api-s4w2qp` (canadaeast) → https://app-relayhub-prod-api-s4w2qp.azurewebsites.net (docs at `/docs`)
  - Azure SQL free offer `sql-relayhub-prod-s4w2qp` / db `relayhub` (canadacentral — the free trial refused SQL servers in Canada East and App Service F1 quota was 0 in Canada Central, so the two regions are mixed on purpose)
  - Key Vault `kv-relayhub-s4w2qp` (connection string + API-key master key), storage `strelayhubs4w2qp` (queues `incoming`, `deliveries`), Log Analytics `log-relayhub-prod-s4w2qp` + App Insights `appi-relayhub-prod`
  - GitHub Actions deploys through app registration `relayhub-github-deploy` (OIDC federated credential on `main`); repo variables `AZURE_CLIENT_ID/TENANT_ID/SUBSCRIPTION_ID`, repo secrets `SQL_ADMIN_PASSWORD`, `API_KEY_MASTER_KEY`.
  - Budget `budget-relayhub`: $5/month, alerts to tminhthong1999@hotmail.com.

## Environment notes

- Windows PC: .NET 10.0.401, Node 24, Docker Desktop 4.94 (WSL2), local SQL container `relayhub-sql`; secrets in user-secrets of `RelayHub.Api`.
- MacBook: needs .NET 10 SDK, Node 24, Docker Desktop; recreate user-secrets (see `CLAUDE.md`); mssql image is amd64 (enable Rosetta in Docker Desktop on Apple Silicon).
- The npm cache on the Windows PC resolves to the current directory and leaves `_cacache/` / `_npx/` folders; they are gitignored.
- The v1 plan docx and v1 diagram are intentionally not in the repo.
- The clickable mockup also exists as a private Claude artifact (Tim's account); the repo copy `mockup/relayhub-mockup.html` is the source.

## Build journal (real problems only — interview material)

- 2026-10-11 — Docker Desktop 4.12 could not start after `wsl --update` installed WSL kernel 6.18 (cgroup v2 only): procd failed on `/sys/fs/cgroup/freezer`. Fix: upgrade Docker Desktop to 4.94.
- 2026-10-11 — Namespace `RelayHub.Infrastructure.Delivery` shadowed the `Delivery` entity inside `RelayHubDbContext` (CS0118). Fix: namespace renamed to `Dispatch`.
- 2026-10-11 — Free-trial subscription quirks: App Service F1 quota is 0 in Canada Central and SQL server creation is refused in Canada East, so the API lives in canadaeast and SQL in canadacentral (`sqlLocation` parameter). Key Vault / Log Analytics names collided (globally unique + 14-day soft delete) → per-subscription `uniqueString` suffix.
- 2026-10-11 — GitHub OIDC login failed with AADSTS700213: with `environment: production` on the job, GitHub presents the subject `repo:<owner>@<ownerId>/<repo>@<repoId>:environment:production` (new id-bearing format), not the branch form. Fixed by adding a federated credential with that exact subject (bootstrap.ps1 now computes it).
- 2026-10-11 — `dotnet run --no-build` right after `dotnet user-secrets init` silently ignored the secrets (UserSecretsId is an assembly attribute) → API fell back to the placeholder connection string and returned 500. Fix: rebuild.
