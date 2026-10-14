/**
 * Request handlers for the dev mock. Implements every endpoint in the Week 3/4 contract against ./data.ts,
 * including the Problem Details the dashboard must handle (401/403/404/409/501/503).
 */
import type { ConversationDetail, ConversationListItem, EventDetail, EventListItem, ReplyWindow, Source, StatsSummary, User } from '../lib/types'
import { createMockDb, hex, type MockDb, type MockEvent, type MockUser } from './data'

export interface MockRequest {
  method: string
  path: string
  query: URLSearchParams
  headers: Headers
  body: unknown
}
export interface MockResponse {
  status: number
  body?: unknown
  contentType?: string
}

const PROBLEM = 'application/problem+json'
const problem = (status: number, title: string, detail?: string, type?: string): MockResponse => ({
  status,
  contentType: PROBLEM,
  body: { type: type ?? 'about:blank', title, status, ...(detail ? { detail } : {}) },
})
const ok = (body?: unknown, status = 200): MockResponse => ({ status, body })
const validation = (field: string, message: string): MockResponse => ({
  status: 400,
  contentType: PROBLEM,
  body: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1', title: 'One or more validation errors occurred.', status: 400, errors: { [field]: [message] } },
})

// --- fake JWT (base64url JSON, unsigned — the mock never leaves the tab) ------------------------------------------
const b64url = (s: string) => btoa(unescape(encodeURIComponent(s))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
function issueToken(db: MockDb, user: MockUser): { token: string; expiresAt: string } {
  const ws = db.workspaces.find((w) => w.id === user.workspaceId)!
  const exp = Math.floor(Date.now() / 1000) + (user.role === 'Owner' ? 7 * 86400 : 2 * 3600)
  const payload = { sub: String(user.id), email: user.email, role: user.role, ws: String(ws.id), wsn: ws.name, demo: String(ws.isDemo), exp, iat: Math.floor(Date.now() / 1000) }
  return { token: `${b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }))}.${b64url(JSON.stringify(payload))}.${hex(43)}`, expiresAt: new Date(exp * 1000).toISOString() }
}
function userDto(db: MockDb, u: MockUser): User {
  const ws = db.workspaces.find((w) => w.id === u.workspaceId)!
  return { id: u.id, email: u.email, role: u.role, workspaceId: ws.id, workspaceName: ws.name, isDemo: ws.isDemo }
}
function authenticate(db: MockDb, req: MockRequest): MockUser | null {
  const auth = req.headers.get('authorization') ?? ''
  const m = /^Bearer (.+)$/.exec(auth)
  if (!m) return null
  const parts = m[1].split('.')
  if (parts.length !== 3) return null
  try {
    const payload = JSON.parse(decodeURIComponent(escape(atob(parts[1].replace(/-/g, '+').replace(/_/g, '/'))))) as { sub: string; exp: number }
    if (payload.exp * 1000 < Date.now()) return null
    return db.users.find((u) => String(u.id) === payload.sub) ?? null
  } catch {
    return null
  }
}

// --- helpers -------------------------------------------------------------------------------------------------
const mask = (v: unknown) => (typeof v === 'string' && v.length > 4 ? `${v.slice(0, 4)}••••••••` : v)
function maskConfig(config: Record<string, unknown> | null): Record<string, unknown> | null {
  if (!config) return null
  const out: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(config)) out[k] = /token|secret|key|url/i.test(k) ? mask(v) : v
  return out
}
function sourceDto(db: MockDb, s: MockDb['sources'][number]): Source {
  void db
  return { id: s.id, type: s.type, name: s.name, keyId: s.keyId, externalAccountId: s.externalAccountId, config: maskConfig(s.config), isEnabled: s.isEnabled, createdAt: s.createdAt }
}
function destinationName(db: MockDb, id: number): string {
  return db.destinations.find((d) => d.id === id)?.name ?? `destination #${id}`
}
function eventListItem(db: MockDb, ev: MockEvent): EventListItem {
  return {
    id: ev.id,
    channel: ev.channel,
    direction: ev.direction,
    senderName: ev.senderName,
    senderHandle: ev.senderHandle,
    text: ev.text,
    receivedAt: ev.receivedAt,
    correlationId: ev.correlationId,
    conversationId: ev.conversationId,
    sourceName: db.sources.find((s) => s.id === ev.sourceId)?.name,
    deliveries: ev.deliveries.map((d) => ({ id: d.id, destinationId: d.destinationId, destinationName: destinationName(db, d.destinationId), status: d.status, attemptCount: d.attemptCount, lastError: d.lastError })),
  }
}
function eventDetail(db: MockDb, ev: MockEvent): EventDetail {
  return {
    id: ev.id,
    channel: ev.channel,
    direction: ev.direction,
    conversationId: ev.conversationId,
    from: { name: ev.senderName, handle: ev.senderHandle },
    text: ev.text,
    receivedAt: ev.receivedAt,
    correlationId: ev.correlationId,
    sourceName: db.sources.find((s) => s.id === ev.sourceId)?.name,
    payload: ev.payload,
    deliveries: ev.deliveries.map((d) => ({ ...d, destinationName: destinationName(db, d.destinationId), attempts: [...d.attempts] })),
  }
}
function replyWindow(db: MockDb, convo: MockDb['conversations'][number], lastInboundAt: string | null): ReplyWindow {
  if (convo.channel === 'instagram' || convo.channel === 'facebook') {
    const closesAt = lastInboundAt ? new Date(new Date(lastInboundAt).getTime() + 24 * 3600_000).toISOString() : null
    const open = closesAt !== null && new Date(closesAt).getTime() > Date.now()
    return { canReply: open, windowClosesAt: closesAt, reason: open ? null : "Meta allows replies only within 24 hours of the customer's last message" }
  }
  if (convo.channel === 'web') {
    const hasEmail = (convo.senderHandle ?? '').includes('@')
    return { canReply: hasEmail, windowClosesAt: null, reason: hasEmail ? null : 'This source has no reply channel' }
  }
  void db
  return { canReply: true, windowClosesAt: null, reason: null }
}
function conversationMessages(db: MockDb, convoId: number) {
  return db.events.filter((e) => e.conversationId === convoId).sort((a, b) => (a.receivedAt < b.receivedAt ? -1 : 1))
}
function paginate<T>(items: T[], query: URLSearchParams) {
  const page = Math.max(1, Number(query.get('page') ?? '1') || 1)
  const pageSize = Math.min(100, Math.max(1, Number(query.get('pageSize') ?? '25') || 25))
  return { items: items.slice((page - 1) * pageSize, page * pageSize), page, pageSize, total: items.length }
}

// --- the router ---------------------------------------------------------------------------------------------
type Handler = (req: MockRequest, params: Record<string, string>, ctx: { db: MockDb; user: MockUser | null }) => MockResponse | Promise<MockResponse>
interface RouteDef {
  method: string
  pattern: RegExp
  keys: string[]
  anonymous?: boolean
  handler: Handler
}

export class MockApi {
  readonly db: MockDb
  private readonly routes: RouteDef[] = []
  /** The wake-up screen is only visible when the API is "cold": the first readiness probes fail on purpose. */
  private coldProbesLeft = 2

  constructor(db = createMockDb()) {
    this.db = db
    this.register()
  }

  async handle(req: MockRequest): Promise<MockResponse> {
    for (const r of this.routes) {
      if (r.method !== req.method) continue
      const m = r.pattern.exec(req.path)
      if (!m) continue
      const params: Record<string, string> = {}
      r.keys.forEach((k, i) => (params[k] = decodeURIComponent(m[i + 1] ?? '')))
      const user = authenticate(this.db, req)
      if (!r.anonymous && !user) return problem(401, 'Unauthorized')
      return r.handler(req, params, { db: this.db, user })
    }
    return problem(404, 'Not Found', `No mock for ${req.method} ${req.path}`)
  }

  private on(method: string, path: string, handler: Handler, opts: { anonymous?: boolean } = {}): void {
    const keys: string[] = []
    const pattern = new RegExp('^' + path.replace(/\{(\w+)\}/g, (_m, k: string) => (keys.push(k), '([^/]+)')) + '/?$')
    this.routes.push({ method, pattern, keys, handler, anonymous: opts.anonymous })
  }

  private register(): void {
    const owner = (ctx: { user: MockUser | null }) => (ctx.user?.role === 'Owner' ? null : problem(403, 'Forbidden', 'Owner role required'))
    const ws = (ctx: { user: MockUser | null }) => ctx.user!.workspaceId
    const isDemoWs = (ctx: { db: MockDb; user: MockUser | null }) => ctx.db.workspaces.find((w) => w.id === ctx.user!.workspaceId)?.isDemo ?? false

    // health (public)
    this.on('GET', '/health', () => ok({ status: 'Healthy' }), { anonymous: true })
    this.on(
      'GET',
      '/health/ready',
      () => {
        if (this.coldProbesLeft > 0) {
          this.coldProbesLeft--
          return problem(503, 'Database is resuming')
        }
        return ok({ status: 'Healthy' })
      },
      { anonymous: true },
    )

    // auth
    this.on(
      'POST',
      '/api/v1/auth/login',
      (req, _p, { db }) => {
        const body = (req.body ?? {}) as { email?: string; password?: string }
        const u = db.users.find((x) => x.role === 'Owner' && x.email.toLowerCase() === (body.email ?? '').toLowerCase() && x.password === body.password)
        if (!u) return problem(401, 'Invalid credentials', 'Email or password is incorrect.')
        return ok({ ...issueToken(db, u), user: userDto(db, u) })
      },
      { anonymous: true },
    )
    this.on(
      'POST',
      '/api/v1/auth/demo',
      (_req, _p, { db }) => {
        const u = db.users.find((x) => x.role === 'Demo')
        if (!u) return problem(503, 'Demo unavailable', 'No demo workspace exists.', 'https://relayhub.dev/problems/demo-unavailable')
        return ok({ ...issueToken(db, u), user: userDto(db, u) })
      },
      { anonymous: true },
    )
    this.on('GET', '/api/v1/auth/me', (_req, _p, { db, user }) => ok(userDto(db, user!)))

    // realtime: 503 so the dashboard's "Live updates unavailable — Refresh" path is exercised
    this.on('POST', '/api/v1/realtime/negotiate', () => problem(503, 'Realtime unavailable', 'SignalR is not configured on this API.', 'https://relayhub.dev/problems/realtime-unavailable'))

    // events
    this.on('GET', '/api/v1/events', (req, _p, ctx) => {
      const q = req.query
      let items = ctx.db.events.filter((e) => e.workspaceId === ws(ctx))
      const channel = q.get('channel')
      if (channel) items = items.filter((e) => e.channel === channel)
      const direction = q.get('direction')
      if (direction === 'in' || direction === 'out') items = items.filter((e) => e.direction === direction)
      const status = q.get('status')
      if (status === 'none') items = items.filter((e) => e.deliveries.length === 0)
      else if (status) items = items.filter((e) => e.deliveries.some((d) => d.status.toLowerCase() === status))
      const text = (q.get('q') ?? '').toLowerCase()
      if (text) items = items.filter((e) => e.text.toLowerCase().includes(text) || (e.senderName ?? '').toLowerCase().includes(text))
      const from = q.get('from')
      if (from) items = items.filter((e) => e.receivedAt >= from)
      const to = q.get('to')
      if (to) items = items.filter((e) => e.receivedAt <= to)
      const page = paginate(items, q)
      return ok({ ...page, items: page.items.map((e) => eventListItem(ctx.db, e)) })
    })
    this.on('GET', '/api/v1/events/{id}', (_req, p, ctx) => {
      const ev = ctx.db.events.find((e) => e.id === Number(p.id) && e.workspaceId === ws(ctx))
      return ev ? ok(eventDetail(ctx.db, ev)) : problem(404, 'Event not found')
    })

    // conversations
    this.on('GET', '/api/v1/conversations', (req, _p, ctx) => {
      const text = (req.query.get('q') ?? '').toLowerCase()
      const channel = req.query.get('channel')
      const list: ConversationListItem[] = []
      for (const c of ctx.db.conversations.filter((x) => x.workspaceId === ws(ctx))) {
        if (channel && c.channel !== channel) continue
        const msgs = conversationMessages(ctx.db, c.id)
        if (msgs.length === 0) continue
        if (text && !((c.senderName ?? '').toLowerCase().includes(text) || (c.senderHandle ?? '').toLowerCase().includes(text) || msgs.some((m) => m.text.toLowerCase().includes(text)))) continue
        const last = msgs[msgs.length - 1]
        const inbound = msgs.filter((m) => m.direction === 'in')
        const outbound = msgs.filter((m) => m.direction === 'out')
        list.push({
          id: c.id,
          channel: c.channel,
          sourceId: c.sourceId,
          sourceName: ctx.db.sources.find((s) => s.id === c.sourceId)?.name ?? '',
          senderName: c.senderName,
          senderHandle: c.senderHandle,
          lastInboundAt: inbound.length ? inbound[inbound.length - 1].receivedAt : null,
          lastOutboundAt: outbound.length ? outbound[outbound.length - 1].receivedAt : null,
          lastMessageAt: last.receivedAt,
          lastText: last.text,
          lastDirection: last.direction,
          messageCount: msgs.length,
        })
      }
      list.sort((a, b) => (a.lastMessageAt < b.lastMessageAt ? 1 : -1))
      return ok(paginate(list, req.query))
    })
    this.on('GET', '/api/v1/conversations/{id}', (_req, p, ctx) => {
      const c = ctx.db.conversations.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (!c) return problem(404, 'Conversation not found')
      const msgs = conversationMessages(ctx.db, c.id)
      const inbound = msgs.filter((m) => m.direction === 'in')
      const outbound = msgs.filter((m) => m.direction === 'out')
      const lastInboundAt = inbound.length ? inbound[inbound.length - 1].receivedAt : null
      const detail: ConversationDetail = {
        id: c.id,
        channel: c.channel,
        sourceId: c.sourceId,
        sourceName: ctx.db.sources.find((s) => s.id === c.sourceId)?.name ?? '',
        senderName: c.senderName,
        senderHandle: c.senderHandle,
        externalThreadId: c.externalThreadId,
        lastInboundAt,
        lastOutboundAt: outbound.length ? outbound[outbound.length - 1].receivedAt : null,
        reply: replyWindow(ctx.db, c, lastInboundAt),
        messages: msgs.map((m) => ({ id: m.id, direction: m.direction, text: m.text, at: m.receivedAt, providerMessageId: m.providerMessageId, correlationId: m.correlationId })),
      }
      return ok(detail)
    })
    this.on('POST', '/api/v1/conversations/{id}/reply', async (req, p, ctx) => {
      const c = ctx.db.conversations.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (!c) return problem(404, 'Conversation not found')
      const text = ((req.body as { text?: string } | null)?.text ?? '').trim()
      if (!text || text.length > 2000) return validation('text', 'Text must be 1 to 2000 characters.')
      const msgs = conversationMessages(ctx.db, c.id)
      const inbound = msgs.filter((m) => m.direction === 'in')
      const lastInboundAt = inbound.length ? inbound[inbound.length - 1].receivedAt : null
      const win = replyWindow(ctx.db, c, lastInboundAt)
      if (!win.canReply) {
        if (win.windowClosesAt) return problem(409, 'Reply window closed', `Meta allows replies only within 24 hours of the customer's last message (last message: ${lastInboundAt}).`, 'https://relayhub.dev/problems/reply-window-closed')
        return problem(501, `No reply channel for ${c.channel}`, win.reason ?? undefined, 'https://relayhub.dev/problems/reply-unsupported')
      }
      if (/\bfail\b/i.test(text)) return problem(502, 'The channel rejected the reply', 'Mock provider error: (#100) Invalid parameter — type "fail" in a reply to see this.', 'https://relayhub.dev/problems/reply-failed')
      await new Promise((r) => setTimeout(r, 600))
      const demo = isDemoWs(ctx)
      const at = new Date().toISOString()
      const ev: MockEvent = {
        id: ++ctx.db.ids.event,
        workspaceId: ws(ctx),
        sourceId: c.sourceId,
        conversationId: c.id,
        channel: c.channel,
        direction: 'out',
        senderName: 'You',
        senderHandle: null,
        text,
        receivedAt: at,
        correlationId: hex(32),
        providerMessageId: demo ? `demo-${hex(8)}` : `m_${hex(16)}`,
        payload: { text },
        deliveries: [],
      }
      ctx.db.events.unshift(ev)
      return ok({ messageId: ev.id, conversationId: c.id, channel: c.channel, status: demo ? 'SentDemo' : 'Sent', providerMessageId: ev.providerMessageId, at })
    })

    // deliveries
    const findDelivery = (ctx: { db: MockDb; user: MockUser | null }, id: number) => {
      for (const ev of ctx.db.events) {
        if (ev.workspaceId !== ws(ctx)) continue
        const d = ev.deliveries.find((x) => x.id === id)
        if (d) return { ev, d }
      }
      return null
    }
    this.on('GET', '/api/v1/deliveries/{id}', (_req, p, ctx) => {
      const f = findDelivery(ctx, Number(p.id))
      return f ? ok({ ...f.d, destinationName: destinationName(ctx.db, f.d.destinationId) }) : problem(404, 'Delivery not found')
    })
    this.on('POST', '/api/v1/deliveries/{id}/replay', (_req, p, ctx) => {
      const f = findDelivery(ctx, Number(p.id))
      if (!f) return problem(404, 'Delivery not found')
      const { d } = f
      if (d.status === 'Delivered') return problem(409, 'Delivery already succeeded')
      if (d.status === 'Pending') return problem(409, 'Delivery is already queued')
      d.status = 'Pending'
      d.nextAttemptAt = null
      // the "worker" completes the replay a little later; without SignalR the user presses Refresh to see it
      setTimeout(() => {
        d.attemptCount += 1
        d.attempts.push({ attemptNo: d.attemptCount, startedAt: new Date().toISOString(), durationMs: 231, httpStatus: 204, error: null })
        d.status = 'Delivered'
        d.lastError = null
      }, 2500)
      return ok({ ...d, destinationName: destinationName(ctx.db, d.destinationId) }, 202)
    })

    // stats
    this.on('GET', '/api/v1/stats/summary', (_req, _p, ctx) => {
      const events = ctx.db.events.filter((e) => e.workspaceId === ws(ctx))
      const dayKey = (iso: string) => iso.slice(0, 10)
      const today = dayKey(new Date().toISOString())
      const weekAgo = new Date(Date.now() - 6 * 86400_000)
      const series: StatsSummary['series'] = []
      for (let i = 0; i < 7; i++) {
        const d = new Date(weekAgo.getTime() + i * 86400_000)
        series.push({ date: dayKey(d.toISOString()), events: 0, delivered: 0, failed: 0 })
      }
      const byChannel = new Map<string, number>()
      let delivered7 = 0
      let dead7 = 0
      for (const e of events) {
        const key = dayKey(e.receivedAt)
        const point = series.find((s) => s.date === key)
        if (point && e.direction === 'in') {
          point.events++
          byChannel.set(e.channel, (byChannel.get(e.channel) ?? 0) + 1)
        }
        for (const d of e.deliveries) {
          if (point) {
            if (d.status === 'Delivered') point.delivered++
            if (d.status === 'DeadLettered') point.failed++
          }
          if (new Date(e.receivedAt) >= weekAgo) {
            if (d.status === 'Delivered') delivered7++
            if (d.status === 'DeadLettered') dead7++
          }
        }
      }
      const todays = events.filter((e) => dayKey(e.receivedAt) === today)
      const allDeliveries = events.flatMap((e) => e.deliveries)
      const summary: StatsSummary = {
        today: {
          events: todays.filter((e) => e.direction === 'in').length,
          replies: todays.filter((e) => e.direction === 'out').length,
          delivered: todays.flatMap((e) => e.deliveries).filter((d) => d.status === 'Delivered').length,
          retrying: allDeliveries.filter((d) => d.status === 'Retrying').length,
          deadLettered: todays.flatMap((e) => e.deliveries).filter((d) => d.status === 'DeadLettered').length,
        },
        total: {
          events: events.length,
          conversations: ctx.db.conversations.filter((c) => c.workspaceId === ws(ctx)).length,
          delivered: allDeliveries.filter((d) => d.status === 'Delivered').length,
          deadLettered: allDeliveries.filter((d) => d.status === 'DeadLettered').length,
        },
        successRate: delivered7 + dead7 === 0 ? null : delivered7 / (delivered7 + dead7),
        byChannel: [...byChannel.entries()].map(([channel, count]) => ({ channel: channel as StatsSummary['byChannel'][number]['channel'], count })).sort((a, b) => b.count - a.count),
        series,
      }
      return ok(summary)
    })

    // sources
    this.on('GET', '/api/v1/sources', (_req, _p, ctx) => ok(ctx.db.sources.filter((s) => s.workspaceId === ws(ctx)).map((s) => sourceDto(ctx.db, s))))
    this.on('GET', '/api/v1/sources/{id}', (_req, p, ctx) => {
      const s = ctx.db.sources.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      return s ? ok(sourceDto(ctx.db, s)) : problem(404, 'Source not found')
    })
    this.on('POST', '/api/v1/sources', (req, _p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const body = (req.body ?? {}) as { name?: string; type?: string; externalAccountId?: string; config?: Record<string, unknown> }
      const name = (body.name ?? '').trim()
      if (!name) return validation('name', 'Name is required')
      if (ctx.db.sources.some((s) => s.workspaceId === ws(ctx) && s.name.toLowerCase() === name.toLowerCase())) return validation('name', 'A source with this name already exists in the workspace')
      const type = (body.type ?? 'generic') as Source['type']
      if (!['generic', 'instagram', 'facebook', 'telegram', 'email'].includes(type)) return validation('type', 'Unknown source type')
      const s = { id: ++ctx.db.ids.source, workspaceId: ws(ctx), type, name, keyId: hex(12), externalAccountId: body.externalAccountId ?? null, config: body.config ?? null, isEnabled: true, createdAt: new Date().toISOString() }
      ctx.db.sources.push(s)
      return ok({ source: sourceDto(ctx.db, s), apiKey: `src_${s.keyId}_${hex(43)}`, ingestUrl: `/api/v1/hooks/${s.name}` }, 201)
    })
    this.on('PUT', '/api/v1/sources/{id}', (req, p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const s = ctx.db.sources.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (!s) return problem(404, 'Source not found')
      const body = (req.body ?? {}) as { name?: string; isEnabled?: boolean; externalAccountId?: string; config?: Record<string, unknown> }
      if (body.name !== undefined) s.name = body.name
      if (body.isEnabled !== undefined) s.isEnabled = body.isEnabled
      if (body.externalAccountId !== undefined) s.externalAccountId = body.externalAccountId
      if (body.config !== undefined) s.config = body.config
      return ok(sourceDto(ctx.db, s))
    })
    this.on('POST', '/api/v1/sources/{id}/rotate-key', (_req, p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const s = ctx.db.sources.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (!s) return problem(404, 'Source not found')
      s.keyId = hex(12)
      return ok({ source: sourceDto(ctx.db, s), apiKey: `src_${s.keyId}_${hex(43)}` })
    })
    this.on('DELETE', '/api/v1/sources/{id}', (_req, p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const i = ctx.db.sources.findIndex((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (i < 0) return problem(404, 'Source not found')
      const [s] = ctx.db.sources.splice(i, 1)
      ctx.db.routes = ctx.db.routes.filter((r) => r.sourceId !== s.id)
      return { status: 204 }
    })

    // destinations
    this.on('GET', '/api/v1/destinations', (_req, _p, ctx) => ok(ctx.db.destinations.filter((d) => d.workspaceId === ws(ctx)).map((d) => ({ ...d, config: maskConfig(d.config) }))))
    this.on('POST', '/api/v1/destinations', (req, _p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const body = (req.body ?? {}) as { name?: string; type?: string; config?: Record<string, unknown> }
      const name = (body.name ?? '').trim()
      if (!name) return validation('name', 'Name is required')
      const type = body.type as MockDb['destinations'][number]['type']
      if (!['discord', 'teams', 'email', 'telegram', 'http'].includes(type)) return validation('type', 'Unknown destination type')
      if (type === 'http' && isDemoWs(ctx)) return problem(403, 'HTTP destinations are not allowed in demo workspaces')
      const cfg = body.config ?? {}
      if ((type === 'discord' || type === 'teams') && typeof cfg.webhookUrl !== 'string') return validation('config.webhookUrl', 'webhookUrl is required')
      if (type === 'email' && typeof cfg.to !== 'string') return validation('config.to', 'to is required')
      if (type === 'telegram' && (typeof cfg.botToken !== 'string' || typeof cfg.chatId !== 'string')) return validation('config', 'botToken and chatId are required')
      if (type === 'http' && !(typeof cfg.url === 'string' && /^https:\/\//.test(cfg.url))) return validation('config.url', 'https URL required')
      const d = { id: ++ctx.db.ids.destination, workspaceId: ws(ctx), type, name, config: cfg, isEnabled: true }
      ctx.db.destinations.push(d)
      return ok({ ...d, config: maskConfig(d.config) }, 201)
    })
    this.on('PUT', '/api/v1/destinations/{id}', (req, p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const d = ctx.db.destinations.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (!d) return problem(404, 'Destination not found')
      const body = (req.body ?? {}) as { name?: string; isEnabled?: boolean; config?: Record<string, unknown> }
      if (body.name !== undefined) d.name = body.name
      if (body.isEnabled !== undefined) d.isEnabled = body.isEnabled
      if (body.config !== undefined) d.config = body.config
      return ok({ ...d, config: maskConfig(d.config) })
    })
    this.on('POST', '/api/v1/destinations/{id}/test', async (_req, p, ctx) => {
      const d = ctx.db.destinations.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (!d) return problem(404, 'Destination not found')
      await new Promise((r) => setTimeout(r, 700))
      if (isDemoWs(ctx)) return ok({ succeeded: true, httpStatus: 204, error: null })
      return d.type === 'teams' ? ok({ succeeded: false, httpStatus: 400, error: 'Workflow rejected the payload (mock)' }) : ok({ succeeded: true, httpStatus: 204, error: null })
    })
    this.on('DELETE', '/api/v1/destinations/{id}', (_req, p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const i = ctx.db.destinations.findIndex((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (i < 0) return problem(404, 'Destination not found')
      const [d] = ctx.db.destinations.splice(i, 1)
      ctx.db.routes = ctx.db.routes.filter((r) => r.destinationId !== d.id)
      return { status: 204 }
    })

    // routes
    this.on('GET', '/api/v1/routes', (_req, _p, ctx) => ok(ctx.db.routes.filter((r) => r.workspaceId === ws(ctx))))
    this.on('POST', '/api/v1/routes', (req, _p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const body = (req.body ?? {}) as { sourceId?: number; destinationId?: number; template?: string; filter?: string }
      if (!ctx.db.sources.some((s) => s.id === body.sourceId && s.workspaceId === ws(ctx))) return validation('sourceId', 'Unknown source')
      if (!ctx.db.destinations.some((d) => d.id === body.destinationId && d.workspaceId === ws(ctx))) return validation('destinationId', 'Unknown destination')
      const r = { id: ++ctx.db.ids.route, workspaceId: ws(ctx), sourceId: body.sourceId!, destinationId: body.destinationId!, template: body.template?.trim() || '{{text}}', filter: body.filter ?? null, isEnabled: true }
      ctx.db.routes.push(r)
      return ok(r, 201)
    })
    this.on('PUT', '/api/v1/routes/{id}', (req, p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const r = ctx.db.routes.find((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (!r) return problem(404, 'Route not found')
      const body = (req.body ?? {}) as { template?: string; filter?: string; isEnabled?: boolean }
      if (body.template !== undefined) r.template = body.template
      if (body.filter !== undefined) r.filter = body.filter || null
      if (body.isEnabled !== undefined) r.isEnabled = body.isEnabled
      return ok(r)
    })
    this.on('DELETE', '/api/v1/routes/{id}', (_req, p, ctx) => {
      const denied = owner(ctx)
      if (denied) return denied
      const i = ctx.db.routes.findIndex((x) => x.id === Number(p.id) && x.workspaceId === ws(ctx))
      if (i < 0) return problem(404, 'Route not found')
      ctx.db.routes.splice(i, 1)
      return { status: 204 }
    })
  }
}
