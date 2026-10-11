<!-- RelayHub project plan, source for build.js. Edit here, then: node build.js ../RelayHub-Project-Plan.docx -->
:::cover
subtitle: Unified Customer Inbox for Small Businesses — built on a reliable Integration Hub
blurb: A live, production-style portfolio project that showcases **C# / .NET**, **ASP.NET Core REST APIs**, **Azure** (App Service, Functions, Azure SQL, Storage Queues, SignalR, Key Vault) and **React + TypeScript** — built to be used for real and demoed to interviewers.
Owner | Tim Tran
Date | October 11, 2026
Version | 2.0 — revised after the mockup review (v1.0 was October 10, 2026)
Status | Mockup done → next step: Phase 1, Core API
Target cost | ≈ $0–1 per month on Azure free tiers
Working title | “RelayHub” — easy to rename later
:::

[[TOC]]

[[PAGEBREAK]]

# What changed in version 2

:::tip Why this revision
After reviewing the clickable mockup, we compared RelayHub with omnichannel inbox tools such as Pancake, Intercom and Meta Business Suite, and checked what Meta actually allows a free, unreviewed app to do. The engine is unchanged; the story, the v1 channel list and a few honest limits are updated.
:::

- **The pitch now leads with the inbox.** RelayHub is described first as a unified customer inbox (the part anyone understands in one sentence) and second as an integration hub (the part that impresses technical interviewers). Same system, clearer story — see section 1.2.
- **Reply from the dashboard moves from v2 into v1** for Facebook Pages and Instagram, through the Meta Send API inside the 24-hour window. Without it RelayHub is only a notification tool; with it, it is a real two-way inbox.
- **The v1 channel list is trimmed** to the website form, Facebook Page, Instagram, inbound email and Telegram. WhatsApp, GitHub and Stripe move to the later list: they add cost or review friction and dilute the story.
- **Meta development-mode limits are documented honestly** (section 4.3). Until the app passes App Review, only people with a role on the app can message it. Real-usage numbers for the resume therefore come from the form, email and Telegram; the Meta channels serve Tim’s own accounts and the demo.
- **Discord and Telegram are explained as borrowed push notifications.** RelayHub is a web app with no mobile app, so it pushes alerts into apps that already buzz the phone (decision 10).
- **Hosting for interviewers is confirmed free**, with a cold-start plan: a cheap keep-warm ping on `/health` (never the database) plus the wake-up screen (section 9.5).
- **Real users are a bonus, not a goal.** Friendly shops can be added as Meta testers later; the project is judged on being live, reliable and demoable.

# 1. Executive Summary

RelayHub is a **unified customer inbox** for a small business. Messages from **Instagram, a Facebook Page, the website contact form, email and Telegram** arrive in **one web dashboard**, and you **reply from that same screen**. Think of the inbox in Pancake, Intercom or Meta Business Suite — but self-hosted, free to run and built as a showcase.

Under the hood it is a small, **production-style integration hub** on Azure. Every message is stored in Azure SQL and forwarded to where the team actually looks (**Discord, Microsoft Teams, email or any REST API**). Failed deliveries are retried automatically, dead-lettered after the last retry and replayable with one click. Nothing gets lost.

The project has two jobs:
- **A real tool.** It runs live for Tim Tran Studio so no freelance lead is ever missed, no matter which channel the customer used.
- **A portfolio centrepiece.** A live, clickable system that demonstrates the exact skills Calgary employers hire .NET full-stack developers for: C#/.NET REST APIs, Azure cloud services, React + TypeScript, CI/CD and reliability engineering.

## 1.1 At a glance

<!-- widths: 20,80 -->
<!-- noheader -->
| **What** | Unified customer inbox (think Pancake, Intercom or Meta Business Suite) on top of a reliable integration hub |
| **Who uses it** | Tim (real daily use for the studio) · interviewers and visitors (public “Try it” demo mode) · later, maybe one or two friendly shops as Meta testers |
| **Backend** | C# / .NET 10 (LTS), ASP.NET Core Web API, Azure Functions (isolated worker), EF Core |
| **Frontend** | React + TypeScript (Vite), TanStack Query, Tailwind CSS, SignalR client |
| **Azure** | App Service, Functions (Flex Consumption), Azure SQL Database (free offer), Storage Queues, Static Web Apps, SignalR Service, Key Vault, Managed Identity, Application Insights |
| **DevOps** | GitHub Actions (OIDC to Azure), Bicep infrastructure-as-code, xUnit + Testcontainers, Vitest |
| **Cost** | ≈ $0–1 per month using Azure free tiers |
| **Timeline** | About 6 weeks part-time; first genuinely useful version after week 1 |
| **Live** | Public demo — no sign-up, nothing to install, works on desktop and phone |

## 1.2 How to describe it (three layers)

The same project told at three depths. Use the first with recruiters, the second with technical interviewers, and the third when they ask “what was the hardest part?”.

<!-- widths: 28,72 -->
| **Audience** | **Say this** |
| **Recruiter (10 seconds)** | “RelayHub is a unified inbox for small businesses: Instagram, Facebook, email and web-form messages in one place, answered from one screen, and never lost.” |
| **Technical interviewer (30 seconds)** | “Underneath it is an event pipeline on Azure: signed webhooks, queue-first ingest, an Azure Functions worker, exponential-backoff retries, dead-lettering, one-click replay and end-to-end tracing. Replies go back out through the Meta Send API.” |
| **“Hardest part?” (2 minutes)** | “Not the UI — making sure a message can never disappear when Meta or Discord is down, while keeping the database off the hot path so the free tier stays free.” Then tell a real story from the build journal. |

:::note
Why not just use Meta Business Suite or Pancake? For a real business you probably should. RelayHub is not trying to win that market; it is a personal tool and a portfolio piece. What it adds: email, Telegram and web-form messages in the same inbox, a visible reliability engine, and a running cost of about $0.
:::

# 2. Purpose & Goals

## 2.1 The problem

Small businesses — including a freelance studio — talk to customers on many channels at once. Messages arrive on Instagram, Facebook, email, Telegram and website forms, each inside a different app. Four things go wrong:
- **Missed leads.** A form submission sits unread for days, and the customer hires someone else.
- **Lost notifications.** If the email or chat service is down at the moment a message arrives, the notification simply disappears — nobody even knows it happened.
- **No single history.** There is no one place to search “what did this customer ask last month?”
- **Slow replies.** Answering means opening three or four apps; the customer who waits longest is the one on the channel you check least.

## 2.2 The solution, in plain English

RelayHub works like a **post office with a front desk**:
- Apps send letters (**events**) to the post office.
- The post office writes every letter into a ledger (**Azure SQL**).
- The front desk shows every letter in one **inbox**, grouped by customer, and lets you **write a reply** that goes back on the channel the customer used (Instagram, Facebook, email or Telegram).
- It also forwards each letter to the right recipients (**Discord, Teams, email…**) based on rules you set, so the team is pinged where they already are.
- If a recipient is not home (the service is down), it tries again later — 30 seconds, 2 minutes, 10 minutes… After 5 failed attempts the letter goes to the **dead-letter shelf**, where you can inspect it and press **Replay**.

## 2.3 Why this project (career goals)

Most portfolio projects are to-do apps or weather apps. RelayHub is deliberately different: it mirrors the day-to-day work of an **integration / backend developer** — connecting systems reliably — which is exactly what Calgary companies in energy, banking and logistics pay .NET developers to do. It is designed to:
- Prove hands-on **Azure** experience across compute, data, messaging, security and monitoring — not just “deployed a website”.
- Prove **C# / .NET** depth: REST API design, EF Core, background processing, dependency injection and resilience patterns.
- Prove **two-way integration**: inbound webhooks and outbound API calls (Meta Send API, Telegram Bot API) with their real-world rules, such as Meta’s 24-hour reply window.
- Prove **React + TypeScript** skills on a real, data-driven dashboard with live updates.
- Produce **true stories** for interviews: real decisions, real trade-offs, real bugs fixed.
- Be **live and clickable**, so anyone reviewing the resume can try it in under a minute.

## 2.4 Success criteria

<!-- widths: 22,78 -->
| **Goal** | **Measured by** |
| **Live & reliable** | Public URL works; the demo flow completes in under 5 minutes, even from a cold start |
| **Real usage** | Studio contact form, inbound email and Telegram connected and used; Tim’s own Instagram and Facebook Page connected in development mode; real (never invented) numbers tracked |
| **Interview ready** | 5-minute demo script rehearsed; answers to the top 10 technical questions prepared |
| **Professional repo** | README with 60-second demo GIF, architecture diagram and setup guide; CI green; tests passing |
| **Free to run** | Azure bill ≤ $1/month, with a budget alert configured |
| **Resume** | Project section with live link, GitHub link and 2–3 metric-backed bullets |

## 2.5 Out of scope for v1

- **Personal Messenger inbox** (friends messaging Tim’s personal profile) — Meta has no API for this at all. Only Pages and professional Instagram accounts can be connected.
- **Letting strangers’ businesses connect their own pages** — needs Meta App Review and probably Business Verification; parked until the core is live (section 4.3).
- **WhatsApp** — needs a dedicated phone number, business verification and per-message fees for template messages; the free test number reaches only 5 people. Moved to later.
- **GitHub and Stripe as sources** — cheap to add, but they dilute the inbox story. Moved to later.
- **A mobile app or our own push notifications** — RelayHub borrows Discord, Telegram and email for that (decision 10).
- Posting to personal Facebook profiles — Meta removed that API.
- Selling RelayHub as a multi-customer SaaS (billing, support, uptime commitments) — possible later.
- Replacing the Instagram / Facebook phone apps — RelayHub is a business inbox and router, not a chat app.

## 2.6 Honest positioning

<!-- widths: 24,38,38 -->
| **Tool** | **What it does well** | **What RelayHub does differently** |
| **Meta Business Suite (free)** | Facebook + Instagram inbox in one place; official; zero setup | Adds email, Telegram and web-form messages; shows deliveries, retries and dead-letters; self-hosted at ≈ $0 |
| **Pancake (Vietnam)** | Facebook, Instagram, Zalo, Shopee, TikTok Shop, orders and POS for shops in Vietnam | Not competing — those channels do not apply in Calgary; RelayHub is a portfolio piece with one or two real users |
| **Intercom / Zendesk / Respond.io** | Full customer-support suites with teams, SLAs and automation | RelayHub is one person’s tool; the resume value is in building the engine, not the suite |

Expect one or two real users from Tim’s own network (the studio, maybe a friendly shop). That is enough: resume metrics need to be real, not large.

# 3. How It Works

## 3.1 Key concepts

<!-- widths: 20,42,38 -->
| **Term** | **Plain-English meaning** | **Example** |
| **Source** | Where messages come from | Instagram Business account, website form |
| **Event** | One incoming item: a message, comment or form submission | Anna sent a DM: “Do you build websites?” |
| **Conversation** | All messages with one customer on one channel, in and out | Anna on Instagram: 3 messages in, 2 replies out |
| **Reply** | An outbound message RelayHub sends to the customer on the original channel | Tim answers Anna from the dashboard; it appears in her Instagram |
| **Reply window** | Meta’s rule: a Page or Instagram account may reply only within 24 hours of the customer’s last message | The dashboard shows a countdown and disables the box when it expires |
| **Destination** | Where RelayHub sends a notification | Tim’s Discord channel |
| **Route** | A rule linking a source to destinations, with a message template | Instagram → Discord + Email: “New DM from {{name}}: {{text}}” |
| **Delivery** | One tracked send of an event to one destination | Event #1042 → Discord |
| **Retry & backoff** | Trying again, waiting longer each time | 30 s → 2 min → 10 min → 30 min → 1 h |
| **Dead-letter** | Where a delivery goes after every retry fails | Shown in the dashboard with a Replay button |
| **Workspace** | An isolated set of sources, routes and data | “Studio” (real data) vs “Demo” (public, fake data) |

## 3.2 Architecture at a glance

![Figure 1 — RelayHub architecture. Numbers 1–7 match the walk-through below; the dashed red path is the reply (step 7).](../architecture.png)

## 3.3 Walk-through: a customer DMs the studio on Instagram

1. The customer sends a DM to the studio’s Instagram Business account. Meta sends a signed webhook (an HTTPS POST) to **RelayHub.Api**.
2. The API checks the `X-Hub-Signature-256` signature, drops the raw payload onto the `incoming` queue and replies **200 OK within milliseconds**. It never touches the database on this hot path.
3. The **Worker** (Azure Functions) picks the message up, converts it into a standard Event, saves it to Azure SQL (duplicates are ignored), attaches it to Anna’s Conversation, finds the matching routes and creates one Delivery per destination.
4. Each Delivery is sent through its connector (Discord, Teams, Email, Telegram, HTTP). Failures are retried with exponential backoff; after 5 attempts the delivery is dead-lettered and Tim is alerted.
5. The Worker pushes a live update through **Azure SignalR Service**.
6. The dashboard shows the new message instantly — no refresh — and Tim’s phone pings in Discord about two seconds after the customer pressed send.
7. Tim opens the conversation and types a reply. **RelayHub.Api** stores it as an outbound message, calls the **Meta Send API** and records the result. Anna sees the reply in Instagram a second later. If the 24-hour window has closed, the API returns a clear Problem Details error and the dashboard explains why.

# 4. Features

## 4.1 MVP feature list

<!-- widths: 22,63,15 -->
| **Feature** | **Description** | **Priority** |
| **Webhook ingest** | Signed endpoints for Meta (Instagram, Facebook Page), Telegram, inbound email and generic sources such as the website form | Must |
| **Unified inbox** | All events in one list; filter by channel, status and date; keyword search; detail view with raw payload | Must |
| **Conversations & reply** | Thread view per customer; reply from the dashboard to Instagram, Facebook Page, Telegram and email; 24-hour window enforced for Meta; outbound messages stored with the thread | Must |
| **Routing rules** | Source → destinations, with message templates such as {{name}}, {{text}}, {{channel}} | Must |
| **Connectors** | Discord, Microsoft Teams, Email (Resend), Telegram and generic HTTP | Must |
| **Reliability** | Retries with exponential backoff, dead-letter, one-click replay, idempotent processing | Must |
| **REST API** | Full CRUD for sources, destinations and routes; interactive OpenAPI docs | Must |
| **Authentication** | Owner login; role-based access (Owner, Demo) | Must |
| **Live updates** | New events, replies and delivery status appear in real time (SignalR) | Should |
| **Dashboard stats** | Today’s totals, delivered / failed / dead-letter counters, 7-day chart | Should |
| **Self-alerting** | When a delivery is dead-lettered, RelayHub tells Tim on Discord | Should |
| **Test button** | “Send test message” for any destination | Should |
| **Keep-warm ping** | GitHub Actions cron hits `/health` every 10 minutes during the day so interviewers rarely see a cold start | Should |
| **WhatsApp** | Inbound and reply through the Cloud API; needs a number and business verification | Later |

## 4.2 Supported channels

<!-- widths: 17,25,24,34 -->
| **Channel** | **As source (in)** | **Reply / destination (out)** | **Setup notes** |
| **Website form** | Yes — signed API key | Reply by email (the form collects an address) | One fetch() call from the site’s server code; works for anyone, no review |
| **Facebook Page** | Yes — Page messages & comments | Yes — Send API, within 24 h | Needs a Facebook Page (free; not a personal profile). Dev mode: only app role holders can message it |
| **Instagram** | Yes — DMs & comments | Yes — Send API, within 24 h | Needs a Business or Creator account (free switch; the account becomes public). Dev mode: Instagram testers only |
| **Email** | Yes — inbound via Cloudflare Email Routing + Worker (or Resend inbound) | Yes — Resend API | Free; anyone can email; the easiest real channel |
| **Telegram** | Yes — bot webhook | Yes — bot message | Free; no review; anyone can message the bot; the best “push to phone” for the owner |
| **Discord** | Later (needs a bot) | Yes — webhook URL | Free; about 2 minutes |
| **Microsoft Teams** | — | Yes — Workflows webhook URL | Free with any Teams account; great for corporate demos |
| **Any REST API** | — | Yes — URL, method, headers, body template | Owner only; blocked in demo mode for security |
| **WhatsApp** | Later | Later | Phone number, business verification, per-message fees for templates; the test number reaches 5 recipients |
| **GitHub / Stripe** | Later — signed webhooks | — | Free (Stripe in test mode); cheap to add once the inbox is done |

:::note
Meta apps (Instagram, Facebook) plus email and Telegram are the **sources you reply to**; chat tools (Discord, Teams) are **destinations** that make the phone buzz. The two halves together are what make RelayHub a unified inbox.
:::

## 4.3 Meta access levels (read before Week 4)

Meta does not care whether you are a registered company. “Business” only means the account type — a Facebook Page, or an Instagram Business / Creator account — and both are free. What matters is the app’s **access level**:

<!-- widths: 22,40,38 -->
| **Stage** | **Who can message your Page / Instagram through RelayHub** | **What it needs** |
| **Development mode (v1)** | Only people with a role on the Meta app: admins, developers, testers and Instagram testers. Tim messaging his own Page works; a stranger’s DM is simply never delivered to the webhook | A Meta developer account and an app — free, about one evening of setup |
| **Live mode after App Review (later)** | Anyone | App Review for pages_messaging and instagram_manage_messages: privacy policy URL, data-deletion callback, screencast of the flow, and usually Business Verification (business documents). Free, but 1–3 weeks |

Consequences for the plan:
- The public demo never depends on Meta: simulators generate correctly signed, Meta-format webhooks.
- “Real usage” numbers come from the website form, email and Telegram, which have no such restriction.
- A friendly shop can be onboarded as a tester, but its customers’ messages will not arrive until App Review passes. Treat App Review as an optional v2 milestone.

## 4.4 Later (v2 ideas)

- WhatsApp inbound and reply (Cloud API) once a number and business verification are worth the effort.
- Meta App Review, so friendly shops can connect their own pages and their customers’ messages flow in.
- GitHub, Stripe, Slack and Google Sheets connectors.
- Web Push notifications (PWA) as a native alternative to the Discord / Telegram ping.
- Page-post and feed notifications (“a new post was published”) — a different webhook, not an inbox feature.
- Microsoft Entra ID single sign-on for the owner account.
- Scheduled “pull” sources — e.g. Alberta pool-price alerts from the AESO public API (a strong talking point with Calgary energy companies).
- Lead status tracking (New → Contacted → Won / Lost) and a simple CRM view.
- Template editor with live preview.

# 5. Architecture & Design Decisions

## 5.1 Components

<!-- widths: 20,25,20,35 -->
| **Component** | **Technology** | **Hosted on** | **Responsibility** |
| **RelayHub.Api** | ASP.NET Core Web API (.NET 10) | App Service F1 (Free) | Webhook ingest (verify + enqueue), REST API for the dashboard, reply endpoint, auth, OpenAPI docs |
| **RelayHub.Worker** | Azure Functions, isolated worker (.NET 10) | Functions Flex Consumption | Queue-triggered processing: normalize, store, route, deliver, retry, dead-letter, push live updates |
| **RelayHub.Core** | C# class library | — | Domain model, routing rules, template rendering, IDestination / ISourceParser / IReplyChannel interfaces |
| **RelayHub.Infrastructure** | C# class library | — | EF Core DbContext and migrations, connector implementations, Meta Send API / Telegram / Resend clients, queue and SignalR clients |
| **Dashboard** | React + TypeScript (Vite) | Static Web Apps (Free) | Landing page, inbox, conversations, event detail, settings, demo mode |
| **Database** | Azure SQL Database | Free offer (serverless) | Source of truth: workspaces, sources, routes, conversations, events, deliveries, attempts |
| **Queues** | Azure Storage Queues | Storage account | Buffer between ingest and processing; poison queue for crashes |
| **Real-time** | Azure SignalR Service | Free tier | Pushes “event created”, “reply sent” and “delivery updated” to open dashboards |
| **Secrets** | Key Vault + Managed Identity | — | Meta app secret and Page tokens, API-key signing key, connector tokens — no passwords in code |
| **Monitoring** | Application Insights (OpenTelemetry) | — | Logs, traces, end-to-end transaction view, failure alerts |

## 5.2 Key design decisions

These decisions are the heart of the interview conversation. Each one has a clear reason and an honest trade-off.

<!-- widths: 22,48,30 -->
| **Decision** | **Why** | **Trade-off accepted** |
| **1. Queue-first ingest** | Webhook senders (Meta, Telegram) expect a fast 200 OK and retry — or disable the webhook — if you are slow. Keeping the database off the hot path keeps ingest fast even while serverless SQL is waking up, and removes the “dual write” problem. | Processing is asynchronous (seconds, not milliseconds) and needs a worker. |
| **2. Storage Queues instead of Service Bus** | Costs fractions of a cent, has built-in poison-queue handling with Functions, and is far more throughput than needed. | No topics or sessions; backoff is done by re-enqueuing with a visibility delay. |
| **3. At-least-once + idempotency** | Queues and webhooks can deliver duplicates. A unique index on (SourceId, ExternalId) makes “save event” safe to repeat. | A destination may very rarely see a duplicate alert — acceptable for notifications. |
| **4. Exponential backoff + dead-letter** | Most failures (rate limits, short outages) fix themselves. Schedule: 30 s, 2 min, 10 min, 30 min, 1 h. The demo workspace uses 2 s, 4 s, 8 s so visitors can watch it happen. | After 5 failures a human decides (Replay). |
| **5. Functions for background work** | App Service F1 has no “Always On”, so background jobs inside the API would stop when the app idles. Functions wake up on queue messages. | Two deployable apps instead of one. |
| **6. Stateless signed API keys** | Key = source ID + HMAC signature made with a master key from Key Vault, so the API verifies it without a database lookup. The Worker drops events from disabled sources. | Revocation is enforced at processing time, not at ingest. |
| **7. Managed Identity everywhere** | API and Worker reach Azure SQL, Storage, Key Vault and SignalR with Entra ID identities — no connection-string passwords. GitHub Actions deploys via OIDC — no stored Azure secrets. | A bit more setup (role assignments, SQL contained users). |
| **8. Never poll the database** | Serverless SQL only stays free while it sleeps. Timers, uptime monitors or auto-refreshing pages that query SQL would keep it awake and burn the free allowance in about two days. Live updates use SignalR; health checks skip SQL. | Every scheduled job has to be thought through. |
| **9. Separate demo workspace** | Visitors get a sandbox with fake data; real customer messages are never visible publicly. | Every query is scoped by WorkspaceId (enforced with an EF Core global query filter). |
| **10. Borrow push notifications** | RelayHub is a web app. A mobile app (APNs/FCM, app stores) or Web Push (PWA, fragile on iPhone) would take weeks and is not the point. Discord, Telegram and email already buzz the phone, so RelayHub pushes alerts into them — the same “connect to Slack” pattern real SaaS products use. | The owner must keep one of those apps installed; the notification itself lives outside RelayHub. |
| **11. Replies are synchronous, deliveries are asynchronous** | A person is waiting when they press Send, and Meta answers immediately (including “window closed”), so the API calls the Send API directly with a short Polly timeout and shows the result. Notifications to Discord or Teams have nobody waiting, so they go through the queue with retries. | A failed reply is not retried in the background; the user presses Send again. Clear errors matter more than persistence here. |

## 5.3 Repository structure

:::code
relay-hub/
├─ src/
│  ├─ RelayHub.Api/               ASP.NET Core Web API
│  ├─ RelayHub.Worker/            Azure Functions (queue triggers)
│  ├─ RelayHub.Core/              domain, routing, templates, interfaces
│  └─ RelayHub.Infrastructure/    EF Core, connectors, Meta/Telegram/Resend clients, queue & SignalR clients
├─ web/                           React + TypeScript dashboard (Vite)
├─ tests/
│  ├─ RelayHub.UnitTests/
│  └─ RelayHub.IntegrationTests/  WebApplicationFactory + Testcontainers (SQL Server)
├─ infra/                         Bicep templates (main.bicep + modules)
├─ .github/workflows/             CI (build + test), CD (deploy), keep-warm ping
└─ docs/                          architecture diagram, demo GIF, decision records, this plan
:::

# 6. REST API Design

## 6.1 Conventions

- Base path `/api/v1`; JSON everywhere; OpenAPI document with interactive docs (Scalar) at `/docs`.
- Errors use RFC 9457 **Problem Details** (`application/problem+json`).
- Lists are paginated: `?page=1&pageSize=25`, with the total count returned.
- Generic ingest returns `202 Accepted`; Meta endpoints return `200 OK` as Meta requires.
- `Idempotency-Key` header honoured on generic ingest; `X-Correlation-Id` returned on every response.
- Auth: **JWT bearer tokens** (users and password hashing via ASP.NET Core Identity); roles `Owner` and `Demo`; CORS locked to the dashboard origin; built-in ASP.NET Core rate limiter.

## 6.2 Endpoints

<!-- widths: 17,33,33,17 -->
| **Method** | **Path** | **Purpose** | **Access** |
| **Ingest (webhooks)** | **Ingest (webhooks)** | **Ingest (webhooks)** | **Ingest (webhooks)** |
| GET | /api/v1/hooks/meta | Meta webhook verification handshake (hub.challenge) | Verify token |
| POST | /api/v1/hooks/meta | Instagram and Facebook Page webhooks | X-Hub-Signature-256 |
| POST | /api/v1/hooks/telegram | Telegram bot updates | X-Telegram-Bot-Api-Secret-Token |
| POST | /api/v1/hooks/email | Inbound email forwarded by the Cloudflare Email Worker | Shared-secret HMAC |
| POST | /api/v1/hooks/{sourceKey} | Generic source, e.g. the website contact form | X-Api-Key |
| **Inbox & conversations** | **Inbox & conversations** | **Inbox & conversations** | **Inbox & conversations** |
| GET | /api/v1/events | List / search events (?channel=&status=&q=&from=&to=) | Owner, Demo |
| GET | /api/v1/events/{id} | Event detail with deliveries and attempts | Owner, Demo |
| GET | /api/v1/conversations | Conversations by customer (?channel=&q=), newest activity first | Owner, Demo |
| GET | /api/v1/conversations/{id} | Messages in and out, plus reply-window status | Owner, Demo |
| POST | /api/v1/conversations/{id}/reply | Send a reply on the original channel | Owner (Demo: mocked) |
| POST | /api/v1/deliveries/{id}/replay | Re-send a failed or dead-lettered delivery | Owner, Demo |
| GET | /api/v1/stats/summary | Dashboard counters and 7-day series | Owner, Demo |
| **Configuration** | **Configuration** | **Configuration** | **Configuration** |
| GET, POST | /api/v1/sources | List / create sources (API key shown once) | Owner |
| GET, PUT, DELETE | /api/v1/sources/{id} | Read / update / delete a source | Owner |
| GET, POST, PUT, DELETE | /api/v1/destinations[/{id}] | Manage destinations | Owner |
| POST | /api/v1/destinations/{id}/test | Send a test message | Owner, Demo |
| GET, POST, PUT, DELETE | /api/v1/routes[/{id}] | Manage routing rules and templates | Owner |
| **Auth, demo and operations** | **Auth, demo and operations** | **Auth, demo and operations** | **Auth, demo and operations** |
| POST | /api/v1/auth/login | Owner login → JWT | Public |
| POST | /api/v1/auth/demo | Start a demo session (Turnstile-protected) → JWT | Public, rate-limited |
| POST | /api/v1/demo/simulate | Generate a realistic, signed Instagram / Facebook / Telegram event | Demo, rate-limited |
| PUT | /api/v1/demo/failure-mode | Toggle “simulate failure” on the demo flaky destination | Demo |
| POST | /api/v1/realtime/negotiate | Get a SignalR connection for live updates | Owner, Demo |
| GET | /health | Liveness — never touches the database; target of the keep-warm ping | Public |
| GET | /health/ready | Readiness incl. database — used only by the wake-up screen | Public |

## 6.3 Example — website form → RelayHub

Request (sent from the studio site’s server code, so the key is never exposed in the browser):

:::code
POST /api/v1/hooks/website-contact
X-Api-Key: src_8f2c…
Idempotency-Key: 4b1e9a7c-2d6f-4f0e-9a51-…
Content-Type: application/json

{
  "name": "Anna Nguyen",
  "email": "anna@example.com",
  "message": "Do you build websites for restaurants?"
}
:::

Response:

:::code
HTTP/1.1 202 Accepted
X-Correlation-Id: 7f3a2c1d9e8b

{ "status": "accepted", "correlationId": "7f3a2c1d9e8b" }
:::

## 6.4 Example — event detail

:::code
GET /api/v1/events/1042

{
  "id": 1042,
  "channel": "instagram",
  "direction": "in",
  "conversationId": 77,
  "from": { "name": "Anna Nguyen", "handle": "@anna.bakes" },
  "text": "Do you build websites for restaurants?",
  "receivedAt": "2026-10-10T16:42:07Z",
  "correlationId": "7f3a2c1d9e8b",
  "deliveries": [
    { "id": 2201, "destination": "Discord", "status": "Delivered", "attempts": 1 },
    { "id": 2202, "destination": "Email", "status": "DeadLettered", "attempts": 5,
      "lastError": "503 Service Unavailable" }
  ]
}
:::

## 6.5 Example — reply from the dashboard

:::code
POST /api/v1/conversations/77/reply
Authorization: Bearer <owner JWT>
Content-Type: application/json

{ "text": "Hi Anna — yes, we build restaurant websites. Can I email you a quote?" }
:::

Response when the reply is accepted by Meta:

:::code
HTTP/1.1 200 OK

{ "messageId": 1043, "channel": "instagram", "status": "Sent", "providerMessageId": "m_AbC123" }
:::

Response when the 24-hour window has closed:

:::code
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{
  "type": "https://relayhub.dev/problems/reply-window-closed",
  "title": "Reply window closed",
  "status": 409,
  "detail": "Meta allows replies only within 24 hours of the customer's last message (last message: 2026-10-09T15:02:11Z)."
}
:::

# 7. Data Model (Azure SQL)

<!-- widths: 18,47,35 -->
| **Table** | **Key columns** | **Notes** |
| **Workspaces** | Id, Name, IsDemo, RetryProfile | “Studio” (real data) and “Demo” (public sandbox) |
| **Users** | Id, Email, PasswordHash, Role, WorkspaceId | ASP.NET Core Identity tables |
| **Sources** | Id, WorkspaceId, Type, Name, KeyId, IsEnabled, CreatedAt | Type: Instagram, Facebook, Telegram, Email, Generic (WhatsApp, GitHub, Stripe later) |
| **Conversations** | Id, WorkspaceId, SourceId, ExternalThreadId, SenderName, SenderHandle, LastInboundAt, LastOutboundAt | One per customer per channel; LastInboundAt drives the 24-hour reply window |
| **Destinations** | Id, WorkspaceId, Type, Name, ConfigJson, IsEnabled | Secrets such as webhook URLs are encrypted at rest (ASP.NET Core Data Protection) |
| **Routes** | Id, WorkspaceId, SourceId, DestinationId, Template, Filter, IsEnabled | Filter example: only DMs, not comments |
| **Events** | Id, WorkspaceId, SourceId, ConversationId, Direction (In / Out), ExternalId, Channel, SenderName, SenderHandle, Text, PayloadJson, ReceivedAt, CorrelationId, ProviderMessageId | Unique index (SourceId, ExternalId) = idempotency; replies are Events with Direction = Out |
| **Deliveries** | Id, EventId, DestinationId, Status, AttemptCount, NextAttemptAt, LastError | Status: Pending, Delivered, Retrying, DeadLettered |
| **DeliveryAttempts** | Id, DeliveryId, AttemptNo, StartedAt, DurationMs, HttpStatus, Error | Powers the timeline in the event detail view |

- EF Core code-first migrations, applied by the CI/CD pipeline with an EF migration bundle.
- Global query filter on WorkspaceId, so demo users can never read Studio data.
- Search: LIKE on Text and SenderName in v1; SQL full-text search later.
- Retention: payloads older than 90 days are purged — keeps storage tiny and limits personal data.

# 8. Reliability, Security & Observability

## 8.1 Reliability

- Queue-first ingest, at-least-once processing and idempotent writes.
- Exponential backoff by re-enqueuing with a visibility delay; dead-letter after 5 attempts; Replay re-queues the same delivery.
- Polly resilience pipeline on outbound HTTP: timeout, retry for transient errors, and a circuit breaker per destination.
- Outbound replies use the same Polly pipeline (timeout plus one quick retry for transient errors) but are not queued; the result is shown to the user immediately.
- Serverless SQL wake-up handled with EF Core `EnableRetryOnFailure` plus a 30-second queue visibility timeout, so messages are not poisoned while the database resumes.
- Queue messages never expire, so nothing is lost even if the database is paused.
- Poison queue for unexpected crashes — RelayHub alerts Tim on Discord when anything lands there.
- Payloads above the 64 KB queue limit go to Blob Storage with only a pointer queued (claim-check pattern).

## 8.2 Security

- Every webhook signature verified against the **raw** request body (HMAC-SHA256, constant-time comparison).
- Meta verify-token handshake implemented for webhook registration; Telegram updates checked against the bot’s secret token header; inbound email signed by the Email Worker with a shared secret.
- Replies are Owner-only; the Demo role’s “reply” is mocked and never reaches Meta or Telegram.
- Secrets live in Key Vault; services use Managed Identity; GitHub Actions uses OIDC — no long-lived credentials anywhere.
- JWT auth with role-based authorization; the Demo role is read-mostly and workspace-scoped.
- SSRF protection: demo users cannot create HTTP destinations; owner HTTP destinations block private and internal IP ranges.
- Per-IP rate limits on ingest, demo login, simulate and reply endpoints; Cloudflare Turnstile on demo login.
- Input validation (FluentValidation), payload size limits, CORS locked to the dashboard origin, HTTPS only.
- Minimal personal data, 90-day retention; all demo data is synthetic.

## 8.3 Observability

- OpenTelemetry → Application Insights (Azure Monitor distro) for logs, traces and metrics.
- W3C trace context is carried inside queue messages, so one customer message becomes **one end-to-end trace**: API → queue → Worker → Discord.
- The correlation ID is shown in the dashboard and can be copied in one click for log search.
- Daily ingestion cap on Application Insights keeps usage inside the free 5 GB per month.

# 9. Making It Live & Impressive

Most reviewers will never clone the repo. The live site has to impress in the **first 60 seconds**, with **zero sign-up** and **nothing to install**.

## 9.1 Visitor journey

1. **Landing page** — loads instantly (static). A one-line pitch, a 60-second demo GIF, the architecture diagram, tech badges, and links to GitHub and the live demo.
2. **“Try the live demo”** — one click, no sign-up (Turnstile runs invisibly). A friendly wake-up screen shows progress while free-tier services start: “Waking API… Waking database… Ready” (typically 20–60 s after idle; usually instant thanks to the keep-warm ping). The limitation becomes a feature that shows you understand serverless.
3. **Inbox (demo workspace)** — pre-filled with realistic sample conversations from Instagram, Facebook, Telegram, email and the website form.
4. **“Simulate a message” buttons** — pick Instagram, Facebook or Telegram; RelayHub generates a correctly signed, provider-format webhook that flows through the real pipeline. It appears live in about 2 seconds.
5. **Reply box** — the visitor answers the simulated DM from the conversation view; the reply appears in the thread marked “Sent (demo)”, with the 24-hour countdown visible. The two-way flow is shown without touching Meta.
6. **“Send your own” form** — the visitor types a message and watches it arrive in real time and get routed to a Discord-style preview panel (demo destinations are mocked, so the public cannot spam real channels).
7. **“Simulate failure” switch** — routes to a built-in flaky endpoint; the visitor watches retries tick at 2 s, 4 s, 8 s → dead-letter → presses Replay → Delivered.
8. **“Under the hood” panel** — for every event: raw JSON payload, delivery timeline, correlation ID and a link to the API docs.

## 9.2 Screens

<!-- widths: 20,45,35 -->
| **Screen** | **What it shows** | **Demo moment** |
| **Landing** | Pitch, demo GIF, architecture diagram, tech stack | Strong first impression in 10 seconds |
| **Inbox** | Unified list with channel icons, status chips, search, filters, live updates | A message appears without refreshing |
| **Conversation** | Thread with inbound and outbound messages, reply box, 24-hour window countdown | “Reply without leaving the page” |
| **Event detail** | Message, sender, raw payload, delivery timeline with every attempt | Retry → dead-letter → Replay |
| **Routes & destinations** | Rules with templates, “send test” button | “A new channel needs no code changes” |
| **Stats** | Today’s totals, success rate, 7-day chart | Proves it is a real, running system |
| **API docs** | Interactive OpenAPI (Scalar) | “It’s a real REST API — call it yourself” |

## 9.3 Protecting the public demo

- Rate limits and Turnstile stop spam that could burn the free quotas.
- Demo destinations and demo replies are mocked — visitors cannot trigger real Discord, Telegram or Instagram messages or call arbitrary URLs.
- Demo data resets lazily (the first demo login each day re-seeds it), so there is no extra scheduled database wake-up.
- Caps: at most 500 demo events; 10 KB per simulated message.

## 9.4 Polish that recruiters notice

- Responsive on phone and desktop — recruiters often open links on mobile.
- Light and dark mode, keyboard accessible, designed empty / loading / error states.
- Custom domain for the dashboard (e.g. hub.<studio-domain>) on Static Web Apps — free, SSL included.

:::tip
Most interviewers will not click the link themselves. The **60-second GIF at the top of the README** and **your own live demo during the interview** are what most people actually see. The live link proves it is real.
:::

## 9.5 Hosting for interviewers is free

- The dashboard (Static Web Apps), the API (App Service F1) and the database (Azure SQL free offer) stay at $0 no matter how many visitors try the demo, within the quotas in section 10.
- Nothing in the public demo touches Meta, so no App Review, business verification or paid phone number is ever needed for it.
- Cold starts are the only visible weakness. Two mitigations: a GitHub Actions cron pings `/health` every 10 minutes between 7 am and 11 pm Mountain time (it never touches the database, so the SQL free budget is untouched, and about 100 requests a day are nothing against the F1 quota), and the wake-up screen covers the database, which still auto-pauses and takes about a minute on the first query.
- Before an interview, open the demo 5 minutes early anyway.

# 10. Hosting & Cost

## 10.1 Azure services and free allowances

<!-- widths: 24,18,40,18 -->
| **Service** | **Tier** | **Free allowance (as of Oct 2026)** | **Expected cost** |
| **App Service (API)** | F1 Free | 60 CPU-minutes/day, 1 GB RAM; no Always On, no custom domain | $0 |
| **Azure Functions (Worker)** | Flex Consumption | 250,000 executions + 100,000 GB-s per month (on-demand) | $0 |
| **Azure SQL Database** | Free offer (serverless) | Per database per month: 100,000 vCore-seconds, 32 GB data, 32 GB backup; up to 10 databases | $0 |
| **Storage account** | Standard LRS | Pay per use (queues, Functions package) | A few cents |
| **Static Web Apps (dashboard)** | Free | 100 GB bandwidth/month; 2 custom domains with SSL | $0 |
| **SignalR Service** | Free | 20 concurrent connections; 20,000 messages/day | $0 |
| **Application Insights** | Pay-as-you-go | First 5 GB/month of ingestion free | $0 |
| **Key Vault** | Standard | Pay per operation (fractions of a cent) | ≈ $0 |
| **GitHub repo + Actions** | Free (public repo) | Unlimited Actions minutes on public repos | $0 |
| **Meta platform (Messenger, Instagram)** | Free | No per-message fees; developer account, app and App Review are free | $0 |
| **Telegram Bot API** | Free | Unlimited at this scale | $0 |
| **Cloudflare Email Routing + Workers** | Free | 100,000 Worker requests/day | $0 |
| **Resend (email)** | Free | Free tier — enough for alerts and replies | $0 |
| **Total** |  |  | ≈ $0–1 / month |

## 10.2 Cost guardrails

:::important
When creating the database, choose **“Auto-pause the database until next month”** for when the free limit runs out — it can then never generate a bill.
Set a **$5 budget alert** in Azure Cost Management on day one.
:::

- Free SQL budget in plain numbers: 100,000 vCore-seconds ≈ **55 hours awake per month** at the 0.5 vCore minimum (about 1.8 h/day). Each wake-up keeps the database on until the auto-pause delay passes, so nothing should wake it on a schedule.
- No timers, uptime monitors or auto-refresh that query SQL; `/health` never touches the database, which is why the keep-warm ping is allowed to hit it.
- Set the Functions instance memory to 512 MB to stretch the GB-s grant four times further.
- Application Insights daily cap (e.g. 100 MB/day).
- The Flex Consumption free grant applies to pay-as-you-go subscriptions: when the Azure free account’s 30-day credit ends, upgrade to pay-as-you-go — free-tier services stay at $0.
- Watch the “Free amount remaining” metric on the database’s Overview page.

## 10.3 Free-tier trade-offs (turned into talking points)

<!-- widths: 35,65 -->
| **Trade-off** | **How RelayHub handles it** |
| **Cold starts (20–60 s after idle)** | Keep-warm ping on `/health`; designed wake-up screen; open the demo 5 minutes before an interview |
| **F1 CPU quota (60 min/day)** | API does minimal work (verify + enqueue); heavy work runs in Functions |
| **F1 has no custom domain** | API stays on \*.azurewebsites.net; the public dashboard gets the custom domain |
| **SQL vCore-second budget** | Queue-first ingest, no polling, lazy demo reset, monitoring metric |
| **Meta development-mode limits** | Simulators for the demo; Tim’s own accounts and invited testers for real use |

:::note
Free offers can change. Re-check the official pricing pages (Appendix B) when creating resources.
:::

# 11. Implementation Roadmap

About **6 weeks part-time** (evenings and weekends). Phase 1 alone is already demo-able — if an interview comes early, show what exists and walk through the rest of the roadmap.

<!-- widths: 18,10,44,28 -->
| **Phase** | **When** | **Deliverables** | **Done when** |
| **0. Mockup & setup** | Done Oct 10 | Clickable HTML mockup of landing, inbox, event detail and demo flow; Azure account + $5 budget alert; public GitHub repo with README skeleton | Mockup reviewed. Still to do: add a conversation / reply screen to the mockup (one evening) before Week 3 |
| **1. Core API (first useful version)** | Week 1 | Solution structure; EF Core + Azure SQL (free offer); Sources / Destinations / Routes CRUD; generic ingest; Discord connector; OpenAPI docs; deploy API via GitHub Actions | Studio contact form → Discord works live |
| **2. Reliability** | Week 2 | Storage Queues + Functions worker; queue-first ingest; retries / backoff, dead-letter, replay; idempotency; Key Vault + Managed Identity; Application Insights tracing | Breaking the Discord URL produces retries → dead-letter → Replay succeeds |
| **3. Dashboard** | Week 3 | React + TypeScript on Static Web Apps: login, inbox, conversations, search, event-detail timeline, stats; SignalR live updates | A new message appears in the browser without refresh |
| **4. Channels & replies** | Week 4 | Meta app (dev mode): Facebook Page + Instagram inbound and reply (Send API, 24-hour window); Telegram bot in and out; inbound email; Teams + Email connectors | A real Instagram DM reaches the inbox and Discord in about 2 s, and a reply typed in the dashboard shows up in Instagram |
| **5. Live demo & quality** | Week 5 | Demo workspace, simulators, mocked replies, failure switch, wake-up screen, keep-warm ping, Turnstile, rate limits; unit + integration tests (Testcontainers), Vitest; Bicep for all resources; CI on every PR | A stranger completes the demo alone in under 5 minutes |
| **6. Launch** | Week 6 | README with 60-s GIF and diagram; connect the studio’s channels; collect real metrics from the form, email and Telegram; update resume; rehearse demo and interview answers | Resume link live; demo rehearsed 3 times |
| **7. Optional** | Later | Meta App Review; onboard one or two friendly shops; WhatsApp | Their customers’ messages arrive in RelayHub |

:::tip Build journal
Keep a **build journal**: for every real problem write the date, symptom, cause and fix. These become your interview stories (“tell me about a hard bug”). Never invent them — real ones are more convincing anyway.
:::

## 11.1 Tools to install

- **Visual Studio Community** (free) with the *ASP.NET and web development* and *Azure development* workloads — includes the .NET SDK, Azure Functions tools, SQL LocalDB and Azurite (local Azure Storage emulator).
- **Azure CLI** and **Azure Functions Core Tools**.
- Already on this PC: Node.js, Docker Desktop (needed for Testcontainers), VS Code, GitHub CLI.

# 12. Skills Showcase Map

How each skill employers look for shows up in the project — and how to prove it in a demo.

<!-- widths: 24,40,36 -->
| **Skill** | **Where it lives in RelayHub** | **How you show it** |
| **C# / .NET 10, ASP.NET Core** | RelayHub.Api: endpoints, middleware, dependency injection | Walk through the ingest endpoint and signature check |
| **REST API design** | Versioning, Problem Details, pagination, idempotency, 202 Accepted | Call an endpoint live from the Scalar docs |
| **EF Core & SQL** | Code-first migrations, indexes, global query filters | Show the unique index that makes processing idempotent |
| **Azure App Service** | API hosting, app settings with Key Vault references, Managed Identity | Azure portal tour of the API’s configuration |
| **Azure Functions** | Queue triggers, isolated worker, Flex Consumption | Show the trigger and the retry / visibility logic |
| **Azure SQL** | Free serverless database, Entra ID authentication | Explain auto-pause and the cost guardrails |
| **Messaging** | Storage Queues, poison queue, backoff, dead-letter | The “simulate failure” demo |
| **Two-way integration** | Meta Send API, Telegram Bot API, the 24-hour window rule, outbound messages stored in the thread | Reply from the dashboard and watch it land on your phone |
| **Real-time** | Azure SignalR Service | A message appears live in the browser |
| **Security** | HMAC verification, JWT + roles, Key Vault, OIDC deploy, SSRF guard, rate limiting | Send a forged webhook → 401 |
| **Observability** | OpenTelemetry, Application Insights end-to-end traces | Open the transaction view for one message |
| **React + TypeScript** | Typed API client, TanStack Query, components, SignalR hook | Code tour of the inbox and conversation pages |
| **Testing** | xUnit, WebApplicationFactory, Testcontainers, Vitest | CI run with all tests green |
| **DevOps / IaC** | GitHub Actions, Bicep, environments | Show the pipeline and main.bicep |
| **Integration thinking** | Connectors, normalization, routing, templates | “Adding Slack = one new class” |

# 13. Demo Script (5 minutes)

<!-- widths: 10,30,60 -->
| **Time** | **Action** | **What to say** |
| **0:00** | Open the landing page | “RelayHub is a unified inbox: Instagram, Facebook, email, Telegram and web-form messages in one place, answered from one screen, and never lost. It runs live on Azure for my freelance studio.” |
| **0:30** | Try the demo → simulate an Instagram DM | “This is a correctly signed Meta webhook going through the real pipeline: the API verifies and enqueues, a Function processes it, SignalR pushes it here.” |
| **1:15** | Open the conversation, type a reply | “Replies go back through the Meta Send API inside the 24-hour window. Here the send is mocked so visitors can’t message real people, but the code path is real up to the connector.” |
| **2:00** | Open the event detail | “Raw payload, delivery timeline, correlation ID — everything is traceable.” |
| **2:30** | Turn on Simulate failure, send again | “Watch: retry at 2, 4, 8 seconds… now it is dead-lettered. I fix the destination, press Replay — delivered.” |
| **3:15** | Send a duplicate and a forged webhook (Scalar / Postman) | “The duplicate is ignored thanks to idempotency; the bad signature is rejected with 401.” |
| **3:50** | Application Insights transaction view | “One customer message is one end-to-end trace across the API, the queue and the Function.” |
| **4:20** | GitHub: pipeline + Bicep | “Everything deploys through GitHub Actions with OIDC — no stored secrets — and the infrastructure is Bicep.” |
| **4:45** | Wrap up | “It costs about a dollar a month on Azure free tiers. Happy to dive into any part.” |

## 13.1 Before every interview

- Open the demo 5 minutes early to wake the services.
- Have tabs ready: dashboard, Scalar API docs, Application Insights (signed in), GitHub repo.
- Keep your phone with Discord visible — the notification ping is a great moment.
- Prepare a 30-second pitch: *“I built RelayHub, a unified customer inbox that runs live on Azure for my freelance studio. It gathers messages from Instagram, Facebook, email, Telegram and web forms into one real-time inbox, lets me reply from there, and it’s built for reliability — queue-based processing, retries, dead-lettering and replay — with a .NET backend and a React TypeScript front end.”*

# 14. Interview Talking Points

<!-- widths: 32,68 -->
| **Likely question** | **Strong short answer** |
| **Why not just use Meta Business Suite or Pancake?** | For a business, those are the right answer. I built RelayHub because I wanted the engine underneath — the part that keeps messages from getting lost — and one inbox where email, Telegram and web forms sit next to Instagram. The value is in what it taught me, and it runs for about $0. |
| **Why put a queue between the API and the processing?** | Webhook senders want a fast 200; processing can be slow or fail. The queue decouples the two, absorbs spikes and lets me retry without losing anything. |
| **What if Meta sends the same webhook twice?** | Processing is idempotent: a unique index on (SourceId, ExternalId) means the second insert is ignored, so there is no duplicate notification. |
| **How do you know a webhook really came from Meta?** | I compute HMAC-SHA256 of the raw body with the app secret and compare it to X-Hub-Signature-256 in constant time. Anything else gets 401. |
| **How does replying work, and what is the 24-hour window?** | A reply is a POST to the Meta Send API with the Page token. Meta only allows it within 24 hours of the customer’s last message, so the API checks LastInboundAt first and returns a 409 Problem Details if the window has closed; the UI shows a countdown. |
| **Why Discord for notifications?** | RelayHub is a web app with no push notifications of its own. Discord, Telegram and email already buzz the phone, so pushing into them was a one-day feature instead of a three-week mobile project — the same “connect to Slack” pattern real SaaS products use. |
| **Can any business connect their page today?** | Not yet. The Meta app is in development mode, so only people with a role on the app can message it. Going public means App Review and business verification — planned, but not needed for the demo, which uses signed simulators. |
| **Discord is down for an hour — what happens?** | Deliveries retry with exponential backoff, up to 1 hour apart. If all 5 attempts fail they are dead-lettered, I get alerted, and I replay with one click. |
| **How do you keep secrets safe?** | Managed Identity to SQL, Storage and Key Vault; secrets only in Key Vault; GitHub deploys via OIDC — no long-lived credentials anywhere. |
| **How would you add a new channel?** | Implement IDestination, ISourceParser or IReplyChannel and register it in DI — the Strategy pattern. The pipeline does not change. |
| **How do you debug a message that never arrived?** | Search the correlation ID in Application Insights; the end-to-end trace shows the API, queue, Function and the outbound call with its error. |
| **How is it free, and what changes at scale?** | Free tiers plus design choices (queue-first, no DB polling). At scale: Service Bus (sessions, scheduled messages), always-ready Functions, provisioned SQL or partitioning, more workers. |
| **How do you keep demo users away from real data?** | A separate demo workspace enforced with an EF Core global query filter on WorkspaceId, plus mocked demo destinations, mocked demo replies and rate limits. |
| **What was the hardest bug?** | Use a real entry from your build journal — for example, messages being poisoned while serverless SQL was resuming, fixed with retry-on-failure and a longer visibility timeout (only if it really happened). |

# 15. Resume Entry (draft)

:::important
Replace every [bracket] with a **real** number once the system is running. Never invent metrics — interviewers ask follow-up questions.
:::

**RelayHub — Unified Customer Inbox & Integration Hub**   |   Live demo · GitHub

*C# · .NET 10 · ASP.NET Core · Azure Functions · Azure SQL · Storage Queues · SignalR · React · TypeScript · GitHub Actions · Bicep*

- Built a live unified customer inbox that aggregates messages from Instagram, Facebook Pages, email, Telegram and web forms into a real-time React/TypeScript dashboard with two-way replies (Meta Send API, Telegram Bot API), processing [X] real events for a freelance web studio.
- Designed a queue-first, event-driven pipeline (ASP.NET Core Web API → Azure Storage Queues → Azure Functions) with HMAC signature verification, idempotent processing, exponential-backoff retries, dead-lettering and one-click replay, achieving [X]% successful delivery.
- Secured and automated the platform with Managed Identity, Key Vault, JWT role-based auth and GitHub Actions (OIDC) + Bicep, and added end-to-end tracing with OpenTelemetry and Application Insights — running for ≈ $1/month on Azure free tiers.

# 16. Risks & Mitigations

<!-- widths: 30,25,45 -->
| **Risk** | **Impact** | **Mitigation** |
| **Meta development-mode limits and App Review friction** | Strangers’ messages never arrive; channel work delayed | Own accounts and invited testers for real use; simulators for the demo; real-usage metrics from the form, email and Telegram; App Review parked as optional |
| **Reply window expired, or Meta rejects a reply** | Confusing failure during a demo | Countdown in the UI; clear 409 Problem Details; demo replies are mocked |
| **A Creator / Business Instagram account must be public** | Tim’s personal Instagram would become public | Use a dedicated studio Instagram account, not the personal one |
| **Cold start during an interview** | Looks broken | Keep-warm ping; wake-up screen; open the demo 5 minutes early; explain the serverless trade-off |
| **Free SQL quota used up** | Database paused until next month | Guardrails in section 10; monitor “free amount remaining”; the queue keeps messages safe |
| **Spam or abuse of the public demo** | Burns quota; offensive content | Turnstile, rate limits, mocked destinations and replies, caps, daily reset |
| **Real customer data exposed** | Privacy breach | Workspace isolation, global query filters, synthetic demo data, retention policy |
| **Scope creep vs job-search time** | Never finished | Strict phases; Phase 1 is already demo-able; v2 ideas parked; WhatsApp, GitHub and Stripe deliberately dropped from v1 |
| **Azure free-offer terms change** | Unexpected cost | Budget alert; re-check pricing pages at setup |
| **Meta API or policy changes** | A channel breaks | Connectors are isolated; the demo relies on simulators, not live Meta |

# 17. Next Steps

1. **Done:** clickable mockup built and reviewed (October 10). Add a conversation / reply screen to it (one evening) so the Week 3 dashboard has a target.
2. Create the Azure account, set the $5 budget alert, and create the public GitHub repo `relay-hub`.
3. Install Visual Studio Community (ASP.NET + Azure workloads), Azure CLI and Azure Functions Core Tools.
4. Free prep for Week 4 that can be done any evening: create a Facebook Page for the studio, create a studio Instagram account and switch it to Creator, create the Meta developer app in development mode, and create a Telegram bot with @BotFather.
5. Start Phase 1: API + Azure SQL + Discord, then connect the studio contact form — the first real, useful version.

# Appendix A — Glossary

<!-- widths: 26,74 -->
| **Term** | **Meaning** |
| **Webhook** | An HTTP call one app makes to another when something happens — a “reverse API”: instead of you asking, they tell you. |
| **REST API** | A set of URLs (endpoints) that programs call with HTTP verbs (GET, POST, PUT, DELETE) to read or change data. |
| **Queue** | A waiting line for work. Producers add messages, workers take them one at a time; if a worker crashes, the message goes back in line. |
| **Idempotency** | Doing the same thing twice gives the same result as doing it once — saving the same message twice still leaves one row. |
| **HMAC signature** | A fingerprint of a message made with a shared secret. If anyone changes the message or doesn’t know the secret, the fingerprint won’t match. |
| **Exponential backoff** | Waiting longer after each failure (30 s, 2 min, 10 min…) so a struggling service has time to recover. |
| **Dead-letter** | A holding area for messages that failed every retry, kept for a human to inspect and replay. |
| **24-hour reply window** | Meta’s rule that a Page or Instagram account may send a free-form reply only within 24 hours of the customer’s last message. |
| **Development mode / App Review** | A Meta app starts in development mode and works only for people with a role on it. App Review is Meta’s approval process that lets the app work for everyone. |
| **Send API** | Meta’s endpoint for sending a message from a Page or Instagram account to a user. |
| **Cold start** | The delay while a sleeping serverless service starts up on its first request. |
| **Managed Identity** | An identity Azure manages for your app, so it can reach other Azure services without passwords. |
| **SignalR** | Microsoft’s real-time library and service that pushes updates from the server to the browser (WebSockets underneath). |
| **Infrastructure as Code (Bicep)** | Describing cloud resources in files, so the whole environment can be recreated with one command. |
| **CI/CD** | Automatically building and testing (CI) and deploying (CD) every time code is pushed. |
| **OIDC federation** | GitHub proves who it is to Azure with a short-lived token, so no Azure password is stored in GitHub. |

# Appendix B — References (verify before setup)

- Azure SQL Database free offer — [learn.microsoft.com/azure/azure-sql/database/free-offer](https://learn.microsoft.com/azure/azure-sql/database/free-offer)
- Azure Functions pricing — [azure.microsoft.com/pricing/details/functions](https://azure.microsoft.com/pricing/details/functions/)
- Azure Functions .NET isolated worker guide — [learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide)
- Azure SignalR Service pricing — [azure.microsoft.com/pricing/details/signalr-service](https://azure.microsoft.com/pricing/details/signalr-service/)
- Azure Static Web Apps quotas — [learn.microsoft.com/azure/static-web-apps/quotas](https://learn.microsoft.com/azure/static-web-apps/quotas)
- Meta Messenger Platform — [developers.facebook.com/docs/messenger-platform](https://developers.facebook.com/docs/messenger-platform)
- Instagram platform (messaging) — [developers.facebook.com/docs/instagram-platform](https://developers.facebook.com/docs/instagram-platform)
- Meta App Review — [developers.facebook.com/docs/app-review](https://developers.facebook.com/docs/app-review)
- Telegram Bot API — [core.telegram.org/bots/api](https://core.telegram.org/bots/api)
- Cloudflare Email Workers — [developers.cloudflare.com/email-routing/email-workers](https://developers.cloudflare.com/email-routing/email-workers/)
- Resend docs — [resend.com/docs](https://resend.com/docs)
