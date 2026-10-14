# RelayHub — hand-off / status

Last updated: 2026-10-14 (Calgary, on the Mac). Keep this file current at the end of every working session.

## Where we are

Week 2 of the 6-week plan (`docs/RelayHub-Project-Plan-v2.docx`, source `docs/plan-src/plan.md`): the reliability engine is built, tested and (as of this commit) being deployed. Week 1 is done (see the Week 1 list below).

Week 2 — done (2026-10-13, Mac):
- **Queue-first ingest**: `POST /api/v1/hooks/{sourceKey}` verifies the HMAC key, checks the body (JSON object, ≤ 32 KB), and enqueues an `IncomingMessage` on the `incoming` Storage Queue — 202 `{status:"queued"}` in milliseconds, no SQL on the hot path. The source-name / enabled check moved to the worker (plan decision 6).
- **Worker** (`RelayHub.Worker`, Functions isolated, .NET 10): `ProcessIncoming` (incoming → Conversation + Event, dedupe on SourceId+ExternalId, one Pending Delivery per route, one `DeliveryMessage` per delivery), `ProcessDelivery` (one message = one attempt; retry = re-enqueue with `DeliveryDispatcher.DelayAfter` as visibility delay; dead-letter after 5 + Discord ops alert), `PoisonAlerts` (watches `incoming-poison` / `deliveries-poison`). The processors live in `RelayHub.Infrastructure/Processing` so the integration tests run them against real SQL without the Functions host.
- **Stale-message guard**: `DeliveryMessage.AfterAttempt` must equal the delivery's AttemptCount, otherwise the worker skips it (duplicates, or a scheduled retry that a replay overtook).
- **Replay**: `POST /api/v1/deliveries/{id}/replay` (+ `GET /api/v1/deliveries/{id}`): Retrying/DeadLettered → Pending + a fresh message; attempt history kept; 409 for Delivered / already Pending.
- **Demo retry profile** wired (`Workspace.RetryProfile == "demo"` → 2/4/8/16/32 s) for Week 5.
- **Ops alerts**: `IOpsAlerts` / `DiscordOpsAlerts` (`RelayHub:OpsDiscordWebhookUrl`, optional; logs a warning when unset). Never throws.
- **Telemetry**: API uses the Azure Monitor OpenTelemetry distro; worker uses Functions OpenTelemetry + Azure Monitor trace exporter + HttpClient instrumentation. The API's W3C traceparent rides in every queue message and each worker function starts its span under it. Both only activate when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set (so local runs and tests stay quiet).
- **Infra**: Bicep adds the Functions **Flex Consumption** plan + app (`func-relayhub-prod-worker-<unique>`, Canada Central, 2048 MB instances, identity-based `AzureWebJobsStorage__accountName`, Key Vault references, deployment container `app-package-worker`), the two poison queues, the optional `RelayHub--OpsDiscordWebhookUrl` secret (GitHub secret `OPS_DISCORD_WEBHOOK_URL`, unset = no alerts), and the worker's role assignments (Key Vault Secrets User; Storage Blob Data Owner, Queue Data Contributor, Storage Account Contributor). `deploy.yml` publishes the worker, deploys it with `Azure/functions-action@v1` (sku flexconsumption) **before** the API, then checks that the four functions are registered.
- **Migration** `AddDeliveryRouteId` (Deliveries.RouteId, nullable): the route's template is re-rendered on every attempt, so fixing a route fixes its replays.
- **Tests**: 21 unit (backoff table incl. demo profile, queue-message JSON round trip) + 6 integration. The 4 Docker tests drive the real processors against SQL Server in Testcontainers with in-memory queues/alerts: queued→stored→delivered + dedupe; 503 ×5 → backoff → dead-letter → one alert → replay → Delivered on attempt 6 → 409 afterwards; stale retry skipped after a replay; forged key 401 / wrong source key dropped / non-object 400 / 40 KB → 413.
- **Local end-to-end verified** on the Mac: API (port 5080) + Azurite + `func start` (Core Tools 4.16.0): smoke.sh stores the event via the worker; a fake Discord webhook → 404 → DeadLettered after 1 attempt → replay → attempt 2 recorded.
- **Live on Azure** (2026-10-13, commit e971442): deploy green end to end (Bicep → migration → worker to Flex Consumption → API → `/health` → 4 functions registered). Live check from the Mac in the Demo workspace (`X-Workspace-Id: 2`): ingest 202 in 0.4 s, worker stored the event ~16 s later, duplicate ignored, forged key 401. Test source deleted afterwards; Studio untouched.
- **`Diagnose` workflow** (`gh workflow run diagnose.yml`, then `gh run view <id> --log`): worker state + registered functions, Key Vault reference resolution for both apps, queue depths, role assignments on the storage account, last hour of Application Insights. The Mac has no `az login`; this is how to look at Azure from here.

Week 2 — not done yet:
- **Discord on the live system**: no destination/route exists in Studio yet (the seeder only creates the `website-contact` source). Create a Discord destination + route on the live API and set the `OPS_DISCORD_WEBHOOK_URL` GitHub secret (next deploy puts it in Key Vault) so dead-letters reach Tim's phone.
- **Azure SQL with Managed Identity** (Entra-only auth, contained user for the API + worker identities, `CREATE USER ... FROM EXTERNAL PROVIDER` from the pipeline). Still SQL auth through Key Vault references today. Needs `az` + an Entra admin on the SQL server; do it from the Windows PC or install `az` on the Mac.
- **Check one end-to-end trace** in Application Insights (API request → worker spans → Discord dependency) and keep a screenshot for the README.
- Claim-check (blob pointer) for payloads > 32 KB — not needed by any v1 channel; ingest returns 413 for now.
- ~~Keep-warm GitHub Actions cron on `/health` (plan: Week 5).~~ Done on branch `w3-infra` (`keep-warm.yml`, see "Week 3/4 — infra").
- The **studio contact form → live API** wiring (source + Discord destination + route on the live API) so real numbers start accumulating.

Week 3/4 — infra (2026-10-14, Mac, branch `w3-infra`, built blind: no `az` on the Mac, validated with the Bicep CLI 0.48.1 only — first real test is the first deploy after the merge):
- **Bicep**: Azure SignalR Service `sigr-relayhub-prod-<unique>` (Free_F1, **Serverless** mode, CORS = the dashboard origin only, region parameter `signalRLocation` = canadacentral) and Static Web App `stapp-relayhub-prod` (Free, `provider: Custom` = no repo binding, `staticWebAppLocation` = eastus2 — SWA is not offered in Canada). Both apps get `SignalR__Endpoint`; the API also gets `RelayHub__DashboardOrigin = https://<swa hostname>` and the three required auth secrets (`RelayHub--JwtSigningKey`, `RelayHub--OwnerEmail`, `RelayHub--OwnerPassword`); optional secrets (`Turnstile--Secret`, `Meta--AppSecret`, `Meta--VerifyToken`, `Telegram--WebhookSecret`, `Email--InboundSecret` → API; `Resend--ApiKey` + plain `Resend__From` → API + worker) follow the ops-webhook rule: the Key Vault reference is only emitted when the secret exists (`optionalSecretSettings` + `filter`/`map`). Role assignments: **SignalR Service Owner** (`7e4f1700-ea5a-4f59-8f37-079cfe29dce3`, copied from the built-in-roles docs) for both identities on the SignalR resource. Outputs `signalRName`, `signalREndpoint`, `staticWebAppName`, `dashboardHostname`, `dashboardUrl`. Existing resource names, SKUs and the ops-webhook pattern are untouched.
- **GitHub secrets the owner must set before the merge deploys** (the Bicep compile fails with "environment variable JWT_SIGNING_KEY does not exist" otherwise): `JWT_SIGNING_KEY` (`gh secret set JWT_SIGNING_KEY --body "$(openssl rand -base64 48)"`), `OWNER_EMAIL`, `OWNER_PASSWORD`. Optional: `TURNSTILE_SECRET`, `META_APP_SECRET`, `META_VERIFY_TOKEN`, `TELEGRAM_WEBHOOK_SECRET`, `EMAIL_INBOUND_SECRET`, `RESEND_API_KEY`; variables `RESEND_FROM`, `API_URL`, `DASHBOARD_URL` (keep-warm). No SWA token secret: `deploy.yml` reads it with `az staticwebapp secrets list` and masks it.
- **deploy.yml**: new env vars into the Bicep step; after the worker-functions check: `setup-node` 24 → `npm ci && npm run build` in `web/` with `VITE_API_BASE_URL=<apiUrl>` → token → `Azure/static-web-apps-deploy@v1` (`action: upload`, `app_location: web/dist`, `skip_app_build`) → dashboard must answer 200 → `POST /api/v1/auth/demo` printed (200 or 503 fine, anything else = warning only, because the API code may merge after the workflow). `web/**` added to the push paths. Worker still deploys before the API.
- **keep-warm.yml**: `*/10 13-23` + `*/10 0-5` UTC ≈ 07:00–23:00 America/Edmonton across MDT/MST (documented as approximate), `GET /health` only (never `/health/ready`), optional dashboard ping, concurrency group `keep-warm`.
- **ci.yml**: second .NET job runs `dotnet test RelayHub.slnx` without the `Category!=Docker` filter (Testcontainers on ubuntu-latest); web job runs `npm run lint --if-present` and `npm test --if-present -- --run` before the build.
- **diagnose.yml**: SignalR show + its role assignments, Static Web App hostname + HTTP status, app setting **names** of API and worker.
- `bootstrap.ps1`: generates/forwards the new secrets, sets the new GitHub secrets/variables, prints the owner password once when it generated it. `infra/README.md` has the full secret table and the SWA / SignalR notes.
- Needs a human with `az` or the first deploy to confirm: SignalR Free_F1 availability in canadacentral (else set `SIGNALR_LOCATION`), that `provider: Custom` + token upload works for the SWA, that *SignalR Service Owner* indeed covers the Management SDK negotiate + REST calls (RBAC propagation can take up to 30 minutes after the first deploy — expect 503 `realtime-unavailable` at first), and that the dashboard agent shipped `web/public/staticwebapp.config.json` (the deploy warns if `dist/` lacks it).

## Week 5 — demo workspace (2026-10-14, branch `w5-demo`)

The public demo of plan section 9, API side. Everything lives in the workspace with `IsDemo = true` (id 2 in a fresh database); nothing in it can reach a real channel.

- **What is seeded** (`DemoSeeder`, called from `DevSeeder.RunAsync` after the Studio seed when `RelayHub:SeedOnStartup` is on; idempotent, keyed on names): sources `demo-website` (generic), `demo-instagram` (ExternalAccountId `demo-ig-17841400000000001`), `demo-facebook` (`demo-page-100000000000001`), `demo-telegram` (`{"botToken":"demo"}`), `demo-email` (`hello@demo.relayhub.dev`); destinations `Discord (mock)` and `Flaky endpoint (mock)` (both type Discord, fake webhook URLs); routes every source → `Discord (mock)` with the default template, plus `demo-website` → `Flaky endpoint (mock)`. Sample data is inserted **once**, only when the demo workspace has zero events: 12 conversations across the five channels, 30 inbound messages over the last 7 days + 3 replies (`Direction = Out`, `SenderName "You"`, `ProviderMessageId demo-xxxxxxxx`) for a small web/design studio (Anna Nguyen @anna.bakes, Marco Rossi, Linh Tran, Jamal Carter, Sofia Alvarez, …), deliveries mostly Delivered (200, 60-180 ms), one Retrying (3 × 503, next attempt scheduled) and one DeadLettered (5 × 503). One Instagram conversation's `LastInboundAt` is 2 h old (reply window open), another 30 h old (closed). Names, ids and caps are constants in `RelayHub.Core.Demo.DemoCatalog`.
- **Mocked deliveries**: `DeliveryProcessor` passes `mock: workspace.IsDemo` to `DeliveryDispatcher.AttemptAsync`, and `POST /destinations/{id}/test` passes it to `SendTestAsync`; with `mock` the dispatcher sends through `DemoSinkConnector` (`Infrastructure/Connectors`) whatever the destination type. The sink answers 200 after 40-120 ms, except when the destination's ConfigJson has `"failing": true`: then `503 simulated outage (demo failure mode)`, retryable, so the demo retry profile (2/4/8/16/32 s) runs and the delivery dead-letters after 5 attempts; Replay succeeds once the switch is off. The sink is deliberately **not** an `IDestinationConnector` (it must never be picked by type for a real workspace).
- **Endpoints** (`DemoEndpoints`, group `/api/v1/demo`, tag "Demo"; every call answers `403 https://relayhub.dev/problems/demo-only` unless the ambient workspace is the demo one):
  - `POST /simulate` `{ "channel": "instagram"|"facebook"|"telegram"|"email"|"web", "text"?: ≤ 500 chars, "senderName"?: string }` → 202 `{ correlationId, channel, sender: { name, handle, email }, text, hint }`. Builds the provider-shaped payload (`DemoPayloads`: Meta webhook batch, Telegram Update, Cloudflare Email Worker post, website form) and enqueues ONE `IncomingMessage` exactly like the verified hook would (same `Kind`, `SourceKey`, `ExternalId` rules: Meta = first 32 hex of SHA-256 of the body, Telegram `tg:<update_id>`, email = messageId, web = new guid). Sender comes from a fixed pool of five regulars, or any typed name mapped to stable ids (SHA-256 of the name), so repeated messages accumulate in one conversation. Rate limit `demo-simulate`: 20 per minute per IP (`DemoRateLimiting.Add`, wired in `Program.cs`). On this branch only `web` is processed end to end by the worker; the Meta / Telegram / email parsers arrive with the channels branch and read these payloads.
  - `GET /failure-mode` → `{ enabled }`; `PUT /failure-mode` `{ "enabled": bool }` → sets `failing` in the ConfigJson of every demo destination whose name contains "Flaky" (other keys kept).
  - `POST /reset` → deletes all events / deliveries / attempts / conversations of the demo workspace, turns the failure switch off and re-runs the sample seed → `{ status, conversations, events, failureMode }`. **Run it before an interview.** (Owner-only once auth lands.) Also run it **once after the first deploy** of this branch: the live demo workspace already holds the Week 2 live-check event, so the "only when there are zero events" seed skips the sample data there.
- **Caps** (plan 9.3): `simulate` text ≤ 500 chars; when the demo workspace holds ≥ 500 events, the oldest 100 (with deliveries + attempts; conversations stay) are deleted before the new message is queued (`DemoSeeder.TrimOldestAsync`).
- **Tests**: 19 unit (payload shapes, ExternalId rules, stable ids, sink ok vs failing) + 6 Docker (`DemoFlowTests`, `X-Workspace-Id: 2`): seed present; simulate web → processors → both mock destinations Delivered without touching the Discord connector; switch on → 503 → retries scheduled 2/4/8/16 s → DeadLettered + one alert → switch off → replay → Delivered on attempt 6; provider channels queued with the hook shapes; cap trims the oldest 100; 403 from Studio; reset.
- Not on this branch (other agents / later): JWT + `RequireAuthorization("Member")` on the group, Turnstile, the dashboard buttons, keep-warm cron, CI running the Docker tests.
## Week 4 — channels (branch `w4-channels`, 2026-10-14, Mac)

Built in parallel with the Week 3 API / dashboard / infra branches against the shared contract of 2026-10-14 (hooks, reply channels,
destination types, settings names). Everything below is in the repo and tested; nothing is connected to a live provider yet (that is the
Week 4 evening work listed under "Point the providers at it").

**Provider hooks** (`src/RelayHub.Api/Endpoints/ProviderHookEndpoints.cs`, anonymous, mapped from `MapIngestEndpoints`). Same rule as the
generic hook: verify, enqueue one `IncomingMessage`, answer in milliseconds, never touch SQL. Each answers `503 "<channel> channel not configured"`
while its secret is unset, and the API / worker log one startup warning listing the unset ones (`ChannelOptionsStartupCheck`).

| Hook | Verifies | Queues | Answer |
|---|---|---|---|
| `GET /api/v1/hooks/meta?hub.mode=subscribe&hub.verify_token=&hub.challenge=` | token == `RelayHub:Meta:VerifyToken` | – | `200 text/plain <challenge>` / 403 |
| `POST /api/v1/hooks/meta` | `X-Hub-Signature-256: sha256=<HMAC(raw body, RelayHub:Meta:AppSecret)>` | `Kind=meta, SourceKey="meta", ExternalId=sha256(body)[..32]` | `200 {}` (Meta needs 200) / 401 |
| `POST /api/v1/hooks/telegram/{sourceKey}` | `X-Telegram-Bot-Api-Secret-Token == RelayHub:Telegram:WebhookSecret` | `Kind=telegram, SourceKey={sourceKey}, ExternalId="tg:"+update_id` | `200 {}` / 401 |
| `POST /api/v1/hooks/email` | `X-RelayHub-Signature: sha256=<HMAC(raw body, RelayHub:Email:InboundSecret)>` | `Kind=email, SourceKey="email", ExternalId=messageId` | `202 {status:"queued"}` / 401 |

Body limit is 32 KB on all hooks (the contract said 64 KB for Meta; a Storage Queue message is 64 KB *after* Base64 + envelope, so the
generic hook's 32 KB ceiling is the honest number — a Meta batch is a few KB in practice).

**Worker parsing** (`src/RelayHub.Infrastructure/Processing/Parsers/`). `IncomingProcessor` picks the `ISourceParser` for `msg.Kind`
(`GenericParser` = the Week 2 path, `MetaWebhookParser`, `TelegramUpdateParser`, `EmailParser`); each returns zero or more `ParsedInbound`
items and the processor runs the conversation upsert / event / dedupe / deliveries loop per item (change tracker cleared between items).
Source lookup: Meta → `Source.Type` Facebook|Instagram **and** `ExternalAccountId == entry.id` (Page id / IG professional account id);
Telegram → the one enabled Telegram source whose `Name == {sourceKey}`; email → Email source with `ExternalAccountId == to` (case-insensitive),
else the single enabled Email source. Threads: PSID / IGSID, `chat.id`, sender address. Echoes (`is_echo`), `is_deleted`, read / delivery /
reaction events, Telegram edits and callback queries produce nothing → new outcome `IncomingOutcome.Ignored` (not a warning). Unknown
source or unusable payload → `Dropped` with a warning; parsers never throw (a throw = poison queue). Attachments are stored as
`[attachment: image]` lines; Meta gives no profile name, so `SenderName` is "Instagram user" / "Facebook user" and `SenderHandle` the scoped id
(a Graph profile lookup is a later improvement). Event `ReceivedAt` / `Conversation.LastInboundAt` use the provider timestamp (what the 24 h
reply window should count from), falling back to our clock when it is missing or implausible.

**Reply channels** (`src/RelayHub.Infrastructure/Replies/`, `IReplyChannel`, synchronous, 10 s timeout, never throw): `MetaReplyChannel`
(`POST graph.facebook.com/v21.0/{ExternalAccountId}/messages`, `Authorization: Bearer <pageAccessToken>`, `messaging_type: RESPONSE`; Meta error
10 / 2018278 → `Fail("Reply window closed", 400)`; registered once for Instagram and once for Facebook), `TelegramReplyChannel` (`sendMessage`),
`EmailReplyChannel` (Resend, from = source `fromAddress` ?? `RelayHub:Resend:From`, subject `Re: <last inbound subject>` else `Reply from <workspace>`,
`In-Reply-To` for threading; registered for Email **and** Generic — a web-form conversation whose `SenderHandle` is an email can be answered by mail).
`ReplyThreadLookup` is the only piece that reads SQL (last inbound payload + workspace name); it is virtual so unit tests stub it.

**Destination connectors** (`src/RelayHub.Infrastructure/Connectors/`): `TeamsConnector` (Workflows webhook, Adaptive Card 1.4 TextBlock),
`EmailConnector` (Resend, `{ to, from?, subject? }`, subject may use `{{name}} {{channel}} {{email}} {{receivedAt}}`), `TelegramConnector`
(`{ botToken, chatId }`), `HttpConnector` (`{ url, method POST|PUT, headers?, bodyTemplate? }`). HTTP destinations: https only, no redirects,
custom headers except connection headers, `bodyTemplate` tokens are JSON-escaped when the template is JSON, and the **SSRF guard**
(`RelayHub.Core.Channels.SsrfGuard`) runs three times — when the destination is saved (`DestinationEndpoints.ValidateConfigAsync`, DNS included),
right before each send (clear error in the attempt history), and inside `SocketsHttpHandler.ConnectCallback`, which connects only to the
address the guard just accepted (DNS rebinding cannot slip through). Refused: loopback, RFC 1918, 169.254/16 (cloud metadata), CGNAT,
multicast, unspecified, IPv6 link/site/unique-local, IPv4-mapped forms, `localhost`, `.local`, `.internal`, dotless names. `POST /destinations`
answers 403 for type `http` in the demo workspace. `ValidateConfig` now covers all five types; 429 / 5xx = retryable like Discord.

**Source configs** (`RelayHub.Core.Channels.SourceConfigs.Validate(SourceType, JsonElement?)`): generic → `{}`; instagram / facebook need
`pageAccessToken` (+ optional `pageName`); telegram needs `botToken` matching `^\d+:[A-Za-z0-9_-]{30,}$`; email optional `fromAddress`.
Typed accessors (`PageAccessToken`, `BotToken`, `FromAddress`) are what the reply channels read. The sources API call site is on the `w3-api` branch.

**Settings** (`ChannelOptions`, section `RelayHub`; Key Vault `A--B` ↔ app setting `A__B` ↔ `A:B`; all optional):

| Setting | GitHub secret (infra branch) | Used by |
|---|---|---|
| `RelayHub:Meta:AppSecret` | `META_APP_SECRET` | API: signature of every Meta webhook body |
| `RelayHub:Meta:VerifyToken` | `META_VERIFY_TOKEN` | API: Meta's one-time GET verification |
| `RelayHub:Telegram:WebhookSecret` | `TELEGRAM_WEBHOOK_SECRET` | API: `secret_token` header on Telegram updates |
| `RelayHub:Email:InboundSecret` | `EMAIL_INBOUND_SECRET` | API: signature from the Cloudflare Email Worker |
| `RelayHub:Resend:ApiKey` | `RESEND_API_KEY` | API (email replies) + worker (email destinations) |
| `RelayHub:Resend:From` | `RESEND_FROM` | default sender, e.g. `RelayHub <inbox@yourdomain.com>` (domain verified in Resend) |

Local dev: `dotnet user-secrets set "RelayHub:Meta:AppSecret" "..."` etc. in `src/RelayHub.Api`; the worker reads the same names from
`local.settings.json` (`RelayHub__Resend__ApiKey`).

**Point the providers at it** (free evening work, nothing in code changes):
- *Meta* (Facebook Page + Instagram): Meta developer app in development mode → Add product **Messenger** (and **Instagram** → Messenger API for
  Instagram). Webhooks → callback URL `https://<api>/api/v1/hooks/meta`, verify token = the value you put in `META_VERIFY_TOKEN`; subscribe the
  Page to `messages`, `messaging_postbacks` (and `messaging_echoes` is fine — echoes are ignored); for Instagram subscribe `messages`. App secret
  (Settings → Basic) → `META_APP_SECRET`. Generate a Page access token with `pages_messaging` (+ `instagram_manage_messages`) and create the
  sources: `{ "name": "studio-page", "type": "facebook", "externalAccountId": "<PAGE_ID>", "config": { "pageAccessToken": "..." } }` and
  `{ "name": "studio-ig", "type": "instagram", "externalAccountId": "<IG_PROFESSIONAL_ACCOUNT_ID>", "config": { "pageAccessToken": "..." } }`.
  Only app role holders can message the Page while the app is in development mode (plan decision).
- *Telegram*: `@BotFather` → `/newbot` → token. Create the source `{ "name": "studio-bot", "type": "telegram", "config": { "botToken": "<token>" } }`,
  pick a random secret for `TELEGRAM_WEBHOOK_SECRET` (`openssl rand -hex 32`), then:
  ```bash
  curl "https://api.telegram.org/bot<token>/setWebhook?url=https://<api>/api/v1/hooks/telegram/studio-bot&secret_token=<TELEGRAM_WEBHOOK_SECRET>&allowed_updates=%5B%22message%22%5D"
  curl "https://api.telegram.org/bot<token>/getWebhookInfo"     # last_error_message tells you if the API refused it
  ```
  For a Telegram *destination* (push to your own phone) message the bot once, read your chat id from `getUpdates` (or `@userinfobot`), then
  `{ "type": "telegram", "config": { "botToken": "<token>", "chatId": "<id>" } }`.
- *Email in*: `infra/cloudflare-email-worker/README.md` (wrangler deploy + Email Routing rule → Worker; same secret in `EMAIL_INBOUND_SECRET`),
  then an `email` source with `externalAccountId` = the inbound address.
- *Email out* (replies + email destinations): Resend account, verify the sending domain (DKIM/SPF records in Cloudflare), API key → `RESEND_API_KEY`,
  sender → `RESEND_FROM`.
- *Teams*: Teams channel → Workflows → "Post to a channel when a webhook request is received" → copy the URL (`*.logic.azure.com`) →
  `{ "type": "teams", "config": { "webhookUrl": "..." } }`.

**Tests**: 137 unit (signature valid / tampered / missing; Meta parser page / instagram / echo / attachment / unknown page / batch; Telegram and
email parsers; SSRF guard addresses + URL rules; `SourceConfigs`; reply channels and connectors against a handler-based fake `HttpClient`) +
14 integration, of which 8 new Docker tests in `tests/RelayHub.IntegrationTests/ChannelFlowTests.cs` (own `ChannelApiFactory` with the channel
secrets; sources / destinations / routes written through the DbContext so the tests do not depend on the REST config endpoints or their auth).

**Known gaps / follow-ups**: no Graph profile lookup for Meta sender names; Telegram bot tokens appear in request URLs (Telegram API design) —
check HttpClient instrumentation redaction before enabling URL logging; the destination-validation integration test calls `POST /destinations`
and will need a bearer token after the auth merge; `IRealtimeNotifier.EventCreatedAsync` is called from `IncomingProcessor.StoreAsync` — if the
API branch added the same call, keep one.
## Week 3 — API (branch `w3-api`, 2026-10-14, Mac; built in parallel with `w3-web`, `w4-channels`, `w3-infra` against the shared contract)

Done on this branch:
- **Auth**: `Users` table (migration `Week3Auth`), `PasswordHasher<User>` hashes, HS256 JWT (`RelayHub.Api/Auth/JwtTokens.cs`), claims `sub email role ws wsn demo`, Owner 7 d / Demo 2 h. `POST /api/v1/auth/login` (10/min/IP), `POST /api/v1/auth/demo` (5/min/IP; Cloudflare Turnstile only when `RelayHub:Turnstile:Secret` is set; 503 `demo-unavailable` without a demo workspace), `GET /api/v1/auth/me`. Policies `Owner` / `Member` on every endpoint group; hooks + login + demo + health + docs anonymous. **`X-Workspace-Id` is removed**: `UseWorkspaceFromToken` sets `AmbientWorkspace` from the `ws` claim. The seeder creates/updates the owner (`RelayHub:OwnerEmail` / `OwnerPassword`, dev fallback `owner@relayhub.local` / `relayhub-dev-owner`) and a `demo@relayhub.local` row (role Demo, unguessable password) that backs demo tokens. CORS from `RelayHub:DashboardOrigin` (comma-separated) + `http://localhost:5173` in Development, exposes `X-Correlation-Id`. 401/403/429 are Problem Details.
- **Inbox API**: `GET /api/v1/events` filters `status` (pending|delivered|retrying|deadlettered|none), `direction` (in|out), `from`/`to`; `sourceName` on events, `destinationName` on deliveries (list + detail). `GET /api/v1/conversations` (channel, q, paging; last message + count), `GET /api/v1/conversations/{id}` with `reply { canReply, windowClosesAt, reason }` from `Core/Conversations/ReplyWindow.cs`, `POST /api/v1/conversations/{id}/reply` → `Sent` through the matching `IReplyChannel` (web-form conversations fall back to the Email channel) or `SentDemo` in the demo workspace; 409 `reply-window-closed`, 501 `reply-unsupported`, 502 `reply-failed`; the reply is an outbound `Event` (`ExternalId = out:<guid>`, `SenderName = You`, no deliveries), `LastOutboundAt` updated, `replySent` pushed. `GET /api/v1/stats/summary` from three grouped queries (today / totals / 7-day success rate, by channel, 7-day series). `destinations/{id}/test` is mocked for demo tokens (response gains `mocked`).
- **Realtime**: `Infrastructure/Realtime/SignalRRealtimeNotifier.cs` on `Microsoft.Azure.SignalR.Management` 1.33.1 (serverless, Transient/REST, camelCase payloads, hub `relayhub`, group `ws-{workspaceId}`), registered in `AddRelayHubMessaging` for API and worker when `SignalR:Endpoint` (DefaultAzureCredential; the identities need **SignalR Service Owner** on the resource) or `SignalR:ConnectionString` is set; otherwise `NullRealtimeNotifier`. Hooks: `IncomingProcessor` → `eventCreated` after Stored (with source name, via a default-interface overload on `IRealtimeNotifier`), `DeliveryProcessor` → `deliveryUpdated` after every attempt and on give-up, replay → `deliveryUpdated` (Pending). `POST /api/v1/realtime/negotiate` → `{ url, accessToken }` (user id = `sub`, user added to the workspace group; user groups persist) or 503 `realtime-unavailable`.
- **Source DTO**: `externalAccountId` + masked `config` on read/create/update, all five source types allowed; `SourceConfigs.Validate` (`Core/Channels/SourceConfigs.cs`, placeholder "JSON object or null") is called on one line in `SourceEndpoints` for the channels branch to replace.
- **Tests**: 35 unit (+ ReplyWindow rules, JWT round trip incl. tampered `ws` claim, SourceConfigs) + 21 integration (18 Docker in one shared SQL Server collection fixture with cached owner/demo tokens and a `RecordingNotifier`: auth, isolation, Source DTO, conversations, reply SentDemo / 409 / 501, stats, negotiate 503, mocked destination test; 3 without a database). `scripts/smoke.sh` logs in first.

Settings this branch reads (Key Vault `A--B` ↔ app setting `A__B`): `RelayHub:JwtSigningKey` (required outside Development, ≥ 32 chars), `RelayHub:OwnerEmail`, `RelayHub:OwnerPassword` (required outside Development), `RelayHub:DashboardOrigin`, `RelayHub:Turnstile:Secret` (optional), `SignalR:Endpoint` or `SignalR:ConnectionString` (optional). **The deploy that merges this branch must ship the three required settings first, or the API throws at startup** (same rule as `ApiKeyMasterKey`).

Not done / for the merge: `IReplyChannel` implementations and the per-type `SourceConfigs` rules come from `w4-channels`; Bicep for SignalR + the new secrets from `w3-infra`; the dashboard from `w3-web`. Rate limiting keys on `RemoteIpAddress` — behind App Service the forwarded-headers middleware must be on (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`) for per-client limits. The unit-test project now references the API project (for `JwtTokens`).

Week 1 — done (2026-10-11, Windows PC):
- Repo, .NET 10 solution, Vite React TS scaffold, GitHub Actions CI (green).
- Data model + EF Core migration `InitialCreate` (workspaces, sources, conversations, destinations, routes, events, deliveries, attempts).
- CRUD: sources (key returned once, rotate-key), destinations (Discord only, config validated and masked, `POST /{id}/test`), routes (templates with `{{name}} {{email}} {{text}} {{channel}} {{source}} {{receivedAt}}`).
- Inbox read endpoints: `GET /api/v1/events`, `GET /api/v1/events/{id}` (deliveries + attempts).
- `/health` (no DB), `/health/ready` (DB), Scalar docs at `/docs`, correlation id on every response.
- API live at https://app-relayhub-prod-api-s4w2qp.azurewebsites.net (`/health`, `/docs`), deployed by `.github/workflows/deploy.yml` (OIDC login → Bicep → publish → EF migration bundle through a temporary SQL firewall rule → App Service → `/health` smoke). Every push to `main` that touches `src/**` or `infra/**` redeploys.
- Plan v2 + regenerated architecture diagram; clickable mockup in `mockup/`.

## Week 3 — dashboard (`web/`, branch `w3-web`, 2026-10-14)

React 19 + TypeScript + Vite 8 SPA in `web/`, built against the Week 3/4 shared contract (JWT auth, inbox/conversations/reply, stats,
SignalR negotiate, configuration API). Dependencies kept to `react-router-dom` 7, `@tanstack/react-query` 5, `@microsoft/signalr`;
tests with `vitest` + Testing Library; styling is one `src/styles.css` with the mockup's CSS variables (light/dark via
`prefers-color-scheme` + a toggle persisted in `localStorage["relayhub.theme"]`).

**Routes**

| Route | Page | Notes |
|---|---|---|
| `/` | Landing | static: pitch, hero (Try the live demo → `/app?demo=1`, Owner login), 3-step "how it works", `public/architecture.png`, tech badges, GitHub + `${API}/docs` links |
| `/login` | Owner login | email + password → `POST /api/v1/auth/login`; "Try the demo instead" → `POST /api/v1/auth/demo` |
| `/app` | Wake-up | polls `GET /health` then `GET /health/ready` with backoff 1→2→4→8→10 s for at most 90 s (the **only** polling in the app), then redirects to `/app/inbox`; with `?demo=1` it performs the demo login first; without a token it shows demo + owner buttons |
| `/app/inbox` | Inbox | `GET /api/v1/events` with URL-backed filters `channel`, `status`, `direction`, `q` (300 ms debounce), `page`; per-delivery status chips |
| `/app/events/:id` | Event detail | message, correlation id + copy, collapsible raw payload, delivery timeline with attempts, **Replay** for Retrying / DeadLettered (409 handled) |
| `/app/conversations` | Conversations | `GET /api/v1/conversations` with channel + search filters |
| `/app/conversations/:id` | Thread | bubbles in/out, reply box (max 2000), **24 h countdown** from `reply.windowClosesAt` (amber under 1 h, disabled + reason when closed), optimistic "Sending…" → "Sent" / "Sent (demo)", Problem Details toasts for 409/501/502 |
| `/app/stats` | Stats | today tiles, 7-day success rate, by-channel bars, inline-SVG 7-day chart (legend, hover tooltip, table view) |
| `/app/settings/{sources,destinations,routes}` | Settings | full CRUD; API key shown once after create/rotate; per-type webhook URL; destination "Send test"; route template editor with placeholder legend + live preview; Demo users see everything read-only |

**Code map**: `src/lib/api.ts` (fetch wrapper: bearer injection, Problem Details → `ApiError {status,type,title,detail}`, 401 → logout +
`/login`), `src/lib/auth.tsx` (token in memory + `localStorage["relayhub.token"]`, JWT claims decoded for instant hydration, confirmed by
`GET /api/v1/auth/me`), `src/lib/realtime.ts` (negotiate → `HubConnectionBuilder` with automatic reconnect; state + `on(event)`),
`src/hooks/useRealtime.tsx` (hub messages → react-query invalidation / cache patches), `src/lib/queries.ts` (query keys, fetchers,
mutations, URL filter parsing), `src/components/*` (chips, primitives, shell, chart), `src/pages/*`, `src/mock/*` (dev mock API).

**No timer polling anywhere** except the wake-up screen: the `QueryClient` has `refetchInterval: false` and window-focus/reconnect refetch
off; data changes arrive through SignalR (`eventCreated` → invalidate events/conversations/stats, `deliveryUpdated` → patch the row +
invalidate the event detail, `replySent` → append to the open thread). When negotiate answers 503 or the connection drops, the header
shows a grey "Live updates unavailable — Refresh" button; refresh = invalidate all queries + one reconnect attempt.

**Environment variables** (Vite, build time; see `web/.env.example`):
- `VITE_API_BASE_URL` — API origin without trailing slash, default `http://localhost:5080`. Production builds must set it to
  `https://app-relayhub-prod-api-s4w2qp.azurewebsites.net` (the SWA deploy workflow on `w3-infra` passes it).
- `VITE_MOCK_API=1` — bake the in-browser mock API in (dev only). At runtime, `?mock=1` on any URL turns it on for the tab
  (`sessionStorage["relayhub.mock"]`), `?mock=0` turns it off.

**Run it**
```bash
cd web && npm install
npm run dev                               # http://localhost:5173 → API on http://localhost:5080 (dotnet run --project src/RelayHub.Api --urls http://localhost:5080)
npm run dev -- --open '/?mock=1'          # no backend: in-memory mock covering every contract endpoint
npm test && npm run lint && npm run build # 30 tests (reply-window countdown, auth store, API error mapping, inbox filters, config helpers)
```
The mock (`src/mock/`) is a `fetch` interceptor for `VITE_API_BASE_URL` only: two workspaces (owner `owner@relayhub.local` /
`relayhub-dev-owner` on "Studio", and the demo workspace "Prairie Bánh Mì"), realistic events with Delivered / Retrying / DeadLettered
deliveries, Meta 24 h windows (one expired), replies (`SentDemo`; type "fail" to get a 502), replay (completes ~2.5 s later → press
Refresh), and `POST /realtime/negotiate` → 503 so the manual-refresh path is what you see. Data resets on reload.

**Static Web Apps**: `web/public/staticwebapp.config.json` has the SPA `navigationFallback` (excluding `/assets/*` and static files) and
security `globalHeaders` (CSP allows Google Fonts, `connect-src https: wss:` for the API + SignalR).

Not done / follow-ups: SignalR is untested against a real hub (the API branch was being built in parallel — only the contract and the
mock were available); the demo simulators / "simulate failure" switch (Week 5) are not in the UI yet; Turnstile token is not collected
on the demo login (the contract makes it optional). The mockup's conversation/reply screen was implemented directly in React rather than
added to `mockup/relayhub-mockup.html`.

## Next steps, in order

1. Finish Week 2 (list above): watch the deploy, then wire the studio contact form to the live API (create a source via `POST /api/v1/sources` on the live API, put the key in the studio site's server config), add a Discord destination + route, and set the `OPS_DISCORD_WEBHOOK_URL` GitHub secret so dead-letters reach Tim's phone.
2. Week 3 — dashboard (`web/`): login (JWT, roles Owner/Demo, replaces the temporary `X-Workspace-Id` header), inbox, conversations with reply box + 24-hour countdown, event detail timeline (attempts + Replay button), stats, SignalR live updates, wake-up screen for cold starts. Add a conversation/reply screen to `mockup/relayhub-mockup.html` first (one evening).
3. Week 4 — channels: Meta app in development mode (Facebook Page + Instagram inbound and reply through the Send API, 24-hour window enforced from `Conversation.LastInboundAt`), Telegram bot (in + out), inbound email via Cloudflare Email Worker, Teams + Resend email connectors. Free prep Tim can do any evening: create a Facebook Page for the studio, a studio Instagram account switched to Creator, the Meta developer app, a Telegram bot via @BotFather.
4. Week 5 — public demo workspace with signed simulators, mocked destinations and replies, failure switch (demo retry profile already exists), Turnstile + rate limits, keep-warm GitHub Actions cron on `/health`, CI runs the Docker tests too. API side done on `w5-demo` (see "Week 5 — demo workspace" above); left: Turnstile, keep-warm cron, CI Docker tests, dashboard buttons.
5. Week 6 — README demo GIF, connect the studio's real channels, collect real metrics (form, email, Telegram), resume entry, rehearse the 5-minute demo (plan section 13).

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
  - Functions Flex Consumption `asp-relayhub-prod-worker` + `func-relayhub-prod-worker-s4w2qp` (canadacentral, parameter `functionsLocation`) — the Week 2 worker; queue-triggered only, no public endpoint. Check it with `az functionapp function list -g rg-relayhub -n func-relayhub-prod-worker-s4w2qp`.
  - Azure SQL free offer `sql-relayhub-prod-s4w2qp` / db `relayhub` (canadacentral — the free trial refused SQL servers in Canada East and App Service F1 quota was 0 in Canada Central, so the two regions are mixed on purpose)
  - Key Vault `kv-relayhub-s4w2qp` (connection string, API-key master key, optional ops webhook; from Week 3 also the JWT key, owner login and the optional channel secrets), storage `strelayhubs4w2qp` (queues `incoming`, `deliveries`, `incoming-poison`, `deliveries-poison`; blob container `app-package-worker`), Log Analytics `log-relayhub-prod-s4w2qp` + App Insights `appi-relayhub-prod`
  - Azure SignalR Service `sigr-relayhub-prod-s4w2qp` (Free_F1, Serverless, canadacentral, parameter `signalRLocation`) — Week 3 realtime; both identities hold SignalR Service Owner on it. Created by the first deploy from `w3-infra`.
  - Static Web App `stapp-relayhub-prod` (Free, eastus2, parameter `staticWebAppLocation`; the hostname `https://<random>.azurestaticapps.net` is a deployment output `dashboardUrl` and the Diagnose workflow prints it) — the Week 3 dashboard, uploaded by `deploy.yml` with the deployment token, no repo binding.
  - GitHub Actions deploys through app registration `relayhub-github-deploy` (OIDC federated credential on `main`); repo variables `AZURE_CLIENT_ID/TENANT_ID/SUBSCRIPTION_ID` (+ optional `API_URL`, `DASHBOARD_URL`, `RESEND_FROM`), repo secrets `SQL_ADMIN_PASSWORD`, `API_KEY_MASTER_KEY`, `JWT_SIGNING_KEY`, `OWNER_EMAIL`, `OWNER_PASSWORD`, optional `OPS_DISCORD_WEBHOOK_URL`, `TURNSTILE_SECRET`, `META_APP_SECRET`, `META_VERIFY_TOKEN`, `TELEGRAM_WEBHOOK_SECRET`, `EMAIL_INBOUND_SECRET`, `RESEND_API_KEY` (full table in `infra/README.md`).
  - Budget `budget-relayhub`: $5/month, alerts to tminhthong1999@hotmail.com.

## Environment notes

- Windows PC: .NET 10.0.401, Node 24, Docker Desktop 4.94 (WSL2), local SQL container `relayhub-sql`; secrets in user-secrets of `RelayHub.Api`.
- MacBook (set up 2026-10-13, Apple Silicon, macOS 26): .NET 10.0.401 via `dotnet-install.sh` into `~/.dotnet` (no sudo; `DOTNET_ROOT` + PATH in `~/.zshrc`), Node 20 (Vite builds fine), Docker engine via **Colima** (`colima start --vm-type vz --vz-rosetta --cpu 4 --memory 6`), not Docker Desktop. `~/.zshrc` exports `DOCKER_HOST=unix://$HOME/.colima/default/docker.sock` and `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock` (Ryuk needs it). Local SQL container `relayhub-sql` (mssql 2022, amd64 under Rosetta, `--restart unless-stopped`); user-secrets recreated. **Run the API on port 5080**: macOS AirPlay Receiver owns port 5000 and answers 403. Clone lives in `~/WebstormProjects/relay-hub`. Azurite container `relayhub-azurite` (ports 10000-10002). Functions Core Tools **4.16.0 via npm** (`npm i -g azure-functions-core-tools@4.16.0`; the Homebrew tap is refused by Homebrew 7 as untrusted, and the `@4` tag installed 4.12.1 whose host is still .NET 8 — see the journal). Azure CLI is not installed on the Mac (no `az login` possible without a browser); Azure checks go through GitHub Actions logs or the live API.
- The npm cache on the Windows PC resolves to the current directory and leaves `_cacache/` / `_npx/` folders; they are gitignored.
- The v1 plan docx and v1 diagram are intentionally not in the repo.
- The clickable mockup also exists as a private Claude artifact (Tim's account); the repo copy `mockup/relayhub-mockup.html` is the source.

## Build journal (real problems only — interview material)

- 2026-10-11 — Docker Desktop 4.12 could not start after `wsl --update` installed WSL kernel 6.18 (cgroup v2 only): procd failed on `/sys/fs/cgroup/freezer`. Fix: upgrade Docker Desktop to 4.94.
- 2026-10-11 — Namespace `RelayHub.Infrastructure.Delivery` shadowed the `Delivery` entity inside `RelayHubDbContext` (CS0118). Fix: namespace renamed to `Dispatch`.
- 2026-10-11 — Free-trial subscription quirks: App Service F1 quota is 0 in Canada Central and SQL server creation is refused in Canada East, so the API lives in canadaeast and SQL in canadacentral (`sqlLocation` parameter). Key Vault / Log Analytics names collided (globally unique + 14-day soft delete) → per-subscription `uniqueString` suffix.
- 2026-10-11 — GitHub OIDC login failed with AADSTS700213: with `environment: production` on the job, GitHub presents the subject `repo:<owner>@<ownerId>/<repo>@<repoId>:environment:production` (new id-bearing format), not the branch form. Fixed by adding a federated credential with that exact subject (bootstrap.ps1 now computes it).
- 2026-10-13 — Right after the first successful deploy, a live ingest returned 202 but no event appeared for 2 minutes, while the deploy log said all four functions were registered. Cause: the worker's role assignments (queue + blob + Key Vault) were created in the same deployment and Azure RBAC takes minutes to propagate, so the Flex scale controller could not read the queue yet. A second check ~8 minutes later was processed in 16 s. Lesson: after a deploy that creates identities/roles, wait before judging; and never delete the test source before the message was processed (the first message was then dropped as "unknown source" — correct behaviour, but it hid the evidence).
- 2026-10-13 — First deploy of the worker failed with `RoleDefinitionDoesNotExist` for the Storage Table Data Contributor GUID I had typed from memory. Built-in role ids must be copied from the docs, never recalled. The worker has no Durable Functions, so the table role was simply dropped. Useful side effect: the error came from the role step, which depends on the Function App — so Flex Consumption had already been created on the free trial in Canada Central.
- 2026-10-13 — `func start` died with `Could not load file or assembly 'Microsoft.Extensions.Options, Version=10.0.0.0'` inside `Microsoft.Azure.WebJobs.Extensions.Storage.Queues`. The Functions SDK generates a host-side `WorkerExtensions.csproj` (net8.0) from the worker extension package: worker `Storage.Queues 5.5.5` maps to host extension `5.3.8` (Mar 2026), which requires `Microsoft.Extensions.Hosting ≥ 10.0.3` — i.e. a .NET 10 Functions host — while Core Tools 4.12.1 still ran a .NET 8 host. Found it by reading `obj/Debug/net10.0/WorkerExtensions/obj/project.assets.json`. Fix: Core Tools 4.16.0 (host on .NET 10.0.12). Alternative if ever needed: pin the worker package to 5.5.4 (→ host 5.3.7, Hosting ≥ 8.0.1).
- 2026-10-13 — `dotnet build` after adding `Azure.Storage.Queues`: CS0433 `DefaultAzureCredential` exists in both `Azure.Core 1.60` and `Azure.Identity 1.17.1`. `Microsoft.Data.SqlClient` pins the old Azure.Identity transitively and the newest Azure.Core already contains the type. Fix: reference `Azure.Identity` 1.21.0 explicitly.
- 2026-10-13 — Smoke script "found" the stored event although the worker was down: the query matched an event from an earlier run. Lesson: every check in a smoke script needs a per-run unique marker (`RUN=smoke-$(date +%s)`).
- 2026-10-13 — Bicep: a Key Vault reference to a secret that does not exist is handed to the app as the literal `@Microsoft.KeyVault(...)` string, which the alert sender would have tried to POST to. The ops-webhook app setting is therefore only emitted when the secret is deployed (`concat([...], opsWebhookSettings)`).
- 2026-10-14 — SignalR RBAC: the obvious pairing "API = SignalR App Server, worker = SignalR REST API Owner" would not have worked for serverless mode. Reading the role definitions (web-and-mobile built-in roles page): *REST API Owner* has `hub/*`, `group/*`, `user/*`, `auth/clientToken/action` but **no** `auth/accessKey/action`, which the SDK's Entra credential path calls to fetch the key it signs tokens with; *App Server* has `auth/accessKey/action` + `serverConnection/write` but no `hub/*`. The managed-identity guide names *SignalR Service Owner* (`SignalR/*`) for "a negotiation server … in Serverless mode", so both identities got that one. Lesson repeated: read the data actions of a role, not its name. Also: `readEnvironmentVariable('X')` without a default makes the bicepparam compile fail with a precise message when a GitHub secret is missing — better than an empty secret in Key Vault.
- 2026-10-13 — First run on the Mac: `curl localhost:5000/health` returned 403 before the API even started. macOS Control Center (AirPlay Receiver) listens on 5000 and 7000. Fix: run on 5080 (smoke.sh comment updated).
- 2026-10-13 — Testcontainers under Colima failed with `invalid mount config for type "bind": stat ~/.colima/default/docker.sock: operation not supported`: Ryuk bind-mounts the Docker socket, but the host-side path is a Colima socket that does not exist inside the VM. Fix: `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock`.
- 2026-10-13 — `secrets.json` written by `dotnet user-secrets` starts with a UTF-8 BOM; a `json.load` in a shell helper threw, the SA password came out empty and the mssql container restart-looped with "password too short". Fix: read with `utf-8-sig`. Lesson: wait for the container to be healthy and check `docker logs` before blaming the image.
- 2026-10-11 — `dotnet run --no-build` right after `dotnet user-secrets init` silently ignored the secrets (UserSecretsId is an assembly attribute) → API fell back to the placeholder connection string and returned 500. Fix: rebuild.
- 2026-10-14 — Making `IncomingProcessor` loop over a Meta batch exposed a latent EF Core trap in the Week 2 duplicate path: after `SaveChanges` fails on the unique (SourceId, ExternalId) index, the rejected `Event` stays tracked as Added, so the *next* item's `SaveChanges` retries the same insert and fails again — one duplicate `mid` would have poisoned every later message in the batch. Week 2 never saw it because one queue message was one event and the method returned right after the catch. Fix: `db.ChangeTracker.Clear()` after each item (success or duplicate), and a Docker test that stores two messages after a duplicate in the same run. Caught while designing the loop, not in production — it still goes in the journal because "why do you clear the change tracker there?" is a fair interview question.
- 2026-10-14 — The contract allowed 64 KB Meta webhook bodies, but a Storage Queue message is capped at 64 KB *after* Base64 encoding (≈ 48 KB of raw bytes) and the `IncomingMessage` envelope JSON-escapes the payload on top of that. A 64 KB body would have passed the hook and then failed at `EnqueueIncomingAsync` with a 500 — Meta would retry and eventually disable the subscription. Kept the generic hook's 32 KB ceiling for all hooks (a Meta batch is a few KB); the claim-check (blob pointer) stays the answer if a channel ever needs more.
- 2026-10-14 — SSRF guard design: validating the URL only when the destination is saved is not enough, because the owner's DNS record can later point at a private address (rebinding) and `HttpClient` resolves the name again at send time. Checking again "right before sending" still leaves a window between the check and the connect. Closed it with `SocketsHttpHandler.ConnectCallback`: the handler itself resolves, vets and connects to the vetted address only, with `AllowAutoRedirect = false` so a public host cannot bounce the request to 169.254.169.254 either.
