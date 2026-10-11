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

Not done in Week 1:
- **Deploy the API to Azure.** Written and compiled, not yet run: `infra/main.bicep` (App Service F1, SQL free offer with auto-pause, Storage + queues, Key Vault RBAC, Log Analytics capped + App Insights, managed-identity role assignments), `infra/budget.bicep` ($5 budget alert), `.github/workflows/deploy.yml` (OIDC login → Bicep → publish → EF migration bundle through a temporary SQL firewall rule → App Service deploy → /health smoke). Bootstrap steps are in `infra/README.md`. Waiting on Tim's Azure free-trial subscription (account tminhthong1999@hotmail.com created 2026-10-11).

## Next steps, in order

1. Azure account → deploy API (finish Week 1). Resources: App Service F1 (API), Azure SQL free offer with "auto-pause until next month", Storage account, Key Vault, Application Insights with daily cap. Put the master key and connection string in Key Vault; app settings use Key Vault references.
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

## Environment notes

- Windows PC: .NET 10.0.401, Node 24, Docker Desktop 4.94 (WSL2), local SQL container `relayhub-sql`; secrets in user-secrets of `RelayHub.Api`.
- MacBook: needs .NET 10 SDK, Node 24, Docker Desktop; recreate user-secrets (see `CLAUDE.md`); mssql image is amd64 (enable Rosetta in Docker Desktop on Apple Silicon).
- The npm cache on the Windows PC resolves to the current directory and leaves `_cacache/` / `_npx/` folders; they are gitignored.
- The v1 plan docx and v1 diagram are intentionally not in the repo.
- The clickable mockup also exists as a private Claude artifact (Tim's account); the repo copy `mockup/relayhub-mockup.html` is the source.

## Build journal (real problems only — interview material)

- 2026-10-11 — Docker Desktop 4.12 could not start after `wsl --update` installed WSL kernel 6.18 (cgroup v2 only): procd failed on `/sys/fs/cgroup/freezer`. Fix: upgrade Docker Desktop to 4.94.
- 2026-10-11 — Namespace `RelayHub.Infrastructure.Delivery` shadowed the `Delivery` entity inside `RelayHubDbContext` (CS0118). Fix: namespace renamed to `Dispatch`.
- 2026-10-11 — `dotnet run --no-build` right after `dotnet user-secrets init` silently ignored the secrets (UserSecretsId is an assembly attribute) → API fell back to the placeholder connection string and returned 500. Fix: rebuild.
