/**
 * In-memory dataset for the dev mock. Two workspaces: 1 = "Studio" (owner), 2 = "Prairie Bánh Mì" (demo).
 * Shapes follow src/lib/types.ts; the handlers in ./handlers.ts mutate this state.
 */
import type { Channel, DeliveryAttempt, DeliveryStatus, DestinationType, Direction, SourceType } from '../lib/types'

export interface MockUser {
  id: number
  email: string
  password: string
  role: 'Owner' | 'Demo'
  workspaceId: number
}
export interface MockWorkspace {
  id: number
  name: string
  isDemo: boolean
}
export interface MockSource {
  id: number
  workspaceId: number
  type: SourceType
  name: string
  keyId: string
  externalAccountId: string | null
  config: Record<string, unknown> | null
  isEnabled: boolean
  createdAt: string
}
export interface MockDestination {
  id: number
  workspaceId: number
  type: DestinationType
  name: string
  config: Record<string, unknown>
  isEnabled: boolean
}
export interface MockRoute {
  id: number
  workspaceId: number
  sourceId: number
  destinationId: number
  template: string
  filter: string | null
  isEnabled: boolean
}
export interface MockConversation {
  id: number
  workspaceId: number
  channel: Channel
  sourceId: number
  senderName: string | null
  senderHandle: string | null
  externalThreadId: string | null
}
export interface MockDelivery {
  id: number
  eventId: number
  destinationId: number
  routeId: number | null
  status: DeliveryStatus
  attemptCount: number
  nextAttemptAt: string | null
  lastError: string | null
  attempts: DeliveryAttempt[]
}
export interface MockEvent {
  id: number
  workspaceId: number
  sourceId: number
  conversationId: number | null
  channel: Channel
  direction: Direction
  senderName: string | null
  senderHandle: string | null
  text: string
  receivedAt: string
  correlationId: string
  providerMessageId: string | null
  payload: unknown
  deliveries: MockDelivery[]
}

export interface MockDb {
  workspaces: MockWorkspace[]
  users: MockUser[]
  sources: MockSource[]
  destinations: MockDestination[]
  routes: MockRoute[]
  conversations: MockConversation[]
  events: MockEvent[]
  ids: { source: number; destination: number; route: number; conversation: number; event: number; delivery: number }
}

const HEX = '0123456789abcdef'
export function hex(n: number): string {
  let s = ''
  for (let i = 0; i < n; i++) s += HEX[Math.floor(Math.random() * 16)]
  return s
}
const minutesAgo = (m: number, now: number) => new Date(now - m * 60_000).toISOString()

function attempt(no: number, at: string, ms: number, http: number | null, error: string | null): DeliveryAttempt {
  return { attemptNo: no, startedAt: at, durationMs: ms, httpStatus: http, error }
}

export function createMockDb(now = Date.now()): MockDb {
  const db: MockDb = {
    workspaces: [
      { id: 1, name: 'Studio', isDemo: false },
      { id: 2, name: 'Prairie Bánh Mì', isDemo: true },
    ],
    users: [
      { id: 1, email: 'owner@relayhub.local', password: 'relayhub-dev-owner', role: 'Owner', workspaceId: 1 },
      { id: 2, email: 'demo@relayhub.local', password: '', role: 'Demo', workspaceId: 2 },
    ],
    sources: [],
    destinations: [],
    routes: [],
    conversations: [],
    events: [],
    ids: { source: 10, destination: 20, route: 30, conversation: 100, event: 1000, delivery: 2000 },
  }

  for (const ws of db.workspaces) seedWorkspace(db, ws.id, now)
  return db
}

function seedWorkspace(db: MockDb, workspaceId: number, now: number): void {
  const demo = workspaceId === 2
  const createdAt = minutesAgo(60 * 24 * 12, now)
  const src = (type: SourceType, name: string, externalAccountId: string | null, config: Record<string, unknown> | null): MockSource => {
    const s: MockSource = { id: ++db.ids.source, workspaceId, type, name, keyId: hex(12), externalAccountId, config, isEnabled: true, createdAt }
    db.sources.push(s)
    return s
  }
  const web = src('generic', 'website-contact', null, null)
  const ig = src('instagram', demo ? 'prairie-instagram' : 'studio-instagram', '17841405822304914', { pageAccessToken: 'EAAB••••••••' })
  const fb = src('facebook', demo ? 'prairie-facebook' : 'studio-facebook', '108512340096711', { pageAccessToken: 'EAAB••••••••' })
  const tg = src('telegram', demo ? 'prairie-bot' : 'studio-bot', 'prairiebanhmi_bot', { botToken: '1234••••••••' })
  const em = src('email', 'inbound-email', demo ? 'hello@prairiebanhmi.example' : 'hello@studio.example', { fromAddress: 'hello@prairiebanhmi.example' })

  const dst = (type: DestinationType, name: string, config: Record<string, unknown>): MockDestination => {
    const d: MockDestination = { id: ++db.ids.destination, workspaceId, type, name, config, isEnabled: true }
    db.destinations.push(d)
    return d
  }
  const discord = dst('discord', 'Discord #orders', { webhookUrl: 'https://discord.com/api/webhooks/1234••••••••', username: 'RelayHub' })
  const teams = dst('teams', 'Teams · Front desk', { webhookUrl: 'https://prod-00.canadacentral.logic.azure.com/••••' })
  const email = dst('email', 'Owner inbox', { to: 'owner@prairiebanhmi.example', subject: 'New message from {{name}}' })
  if (!demo) dst('telegram', 'Telegram ops', { botToken: '5678••••••••', chatId: '-1001234567890' })

  const route = (sourceId: number, destinationId: number, template: string): MockRoute => {
    const r: MockRoute = { id: ++db.ids.route, workspaceId, sourceId, destinationId, template, filter: null, isEnabled: true }
    db.routes.push(r)
    return r
  }
  const T = '**{{name}}** via {{channel}} ({{source}})\n{{text}}'
  const rWeb1 = route(web.id, discord.id, T)
  const rWeb2 = route(web.id, email.id, 'From {{name}} <{{email}}>:\n{{text}}')
  const rIg = route(ig.id, discord.id, T)
  const rFb = route(fb.id, discord.id, T)
  const rTg = route(tg.id, teams.id, T)
  const rEm = route(em.id, teams.id, T)
  const routesFor: Record<number, MockRoute[]> = { [web.id]: [rWeb1, rWeb2], [ig.id]: [rIg], [fb.id]: [rFb], [tg.id]: [rTg], [em.id]: [rEm] }

  const sourceByChannel: Record<Channel, MockSource> = { web, instagram: ig, facebook: fb, telegram: tg, email: em }

  // channel, sender, handle, external thread, [text, minutesAgo, direction, deliveryPlan][]
  type Plan = 'ok' | 'retrying' | 'dead' | 'pending'
  type Msg = [text: string, minutesAgo: number, direction?: Direction, plan?: Plan]
  const threads: [Channel, string, string, string, Msg[]][] = [
    ['telegram', 'Minh Tran', '@minhtran', '88123456', [['Can I order 3 lemongrass chicken bánh mì for pickup in 30 minutes? Name is Minh.', 4, 'in', 'pending']]],
    [
      'instagram',
      'Linh Nguyen',
      '@linh.eats.yyc',
      '1784140582230491401',
      [
        ['Do you have vegan options? Bringing 4 friends on Saturday!', 38, 'in', 'ok'],
        ['Yes — tofu and mushroom bánh mì, plus vegan spring rolls. See you Saturday!', 31, 'out'],
        ['Perfect, thank you!! 🙏', 18, 'in', 'ok'],
      ],
    ],
    [
      'web',
      'Sarah Mitchell',
      'sarah.mitchell@example.com',
      'sarah.mitchell@example.com',
      [['Hi! We’d like a catering quote for a team lunch on Oct 23. About 40 people, half vegetarian. Do you deliver downtown?', 52, 'in', 'retrying']],
    ],
    ['facebook', 'Daniel Okafor', 'Facebook user', '2409871234567890', [['Are you open on Thanksgiving Monday, Oct 12?', 100, 'in', 'ok'], ['We are! 10am to 6pm.', 94, 'out']]],
    ['email', 'Rachel Kim', 'rachel.kim@example.com', 'rachel.kim@example.com', [['Gift cards?\n\nDo you sell gift cards? I’m looking for five $25 cards for my staff.', 19 * 60 + 12, 'in', 'dead']]],
    ['facebook', 'Hoa Pham', 'Facebook user', '2409879999999999', [['Cảm ơn nhiều! Bánh mì hôm qua ngon lắm.', 30 * 60 + 4, 'in', 'ok']]],
    ['telegram', 'Kim Vo', '@kimvo', '88199999', [['Running 10 min late for my pickup, sorry!', 26 * 60, 'in', 'ok'], ['No problem, it is waiting for you.', 25 * 60 + 50, 'out']]],
    ['instagram', 'Emily Chen', '@emily.c.yyc', '1784140582230491402', [['Hi! Are you hiring part-time for weekends?', 3 * 24 * 60 + 15, 'in', 'ok']]],
    ['web', 'Anonymous visitor', '', '', [['Test from the contact form', 5 * 24 * 60, 'in', 'ok']]],
  ]

  for (const [channel, name, handle, thread, msgs] of threads) {
    const source = sourceByChannel[channel]
    const convo: MockConversation = {
      id: ++db.ids.conversation,
      workspaceId,
      channel,
      sourceId: source.id,
      senderName: name,
      senderHandle: handle || null,
      externalThreadId: thread || null,
    }
    db.conversations.push(convo)
    for (const [text, mins, direction = 'in', plan = 'ok'] of msgs) {
      const receivedAt = minutesAgo(mins, now)
      const ev: MockEvent = {
        id: ++db.ids.event,
        workspaceId,
        sourceId: source.id,
        conversationId: convo.id,
        channel,
        direction,
        senderName: direction === 'out' ? 'You' : name,
        senderHandle: direction === 'out' ? null : handle || null,
        text,
        receivedAt,
        correlationId: hex(32),
        providerMessageId: direction === 'out' ? (demo ? `demo-${hex(8)}` : `m_${hex(16)}`) : null,
        payload: payloadFor(channel, name, handle, text, receivedAt),
        deliveries: [],
      }
      if (direction === 'in') {
        for (const r of routesFor[source.id] ?? []) ev.deliveries.push(makeDelivery(db, ev.id, r, plan, receivedAt, now))
      }
      db.events.push(ev)
    }
  }

  // A few older events for the 7-day chart.
  for (let day = 1; day <= 6; day++) {
    const count = demo ? 2 + ((day * 7) % 5) : 1 + (day % 3)
    for (let i = 0; i < count; i++) {
      const channel: Channel = (['web', 'instagram', 'facebook', 'telegram', 'email'] as Channel[])[(day + i) % 5]
      const source = sourceByChannel[channel]
      const receivedAt = minutesAgo(day * 24 * 60 + i * 97 + 30, now)
      const ev: MockEvent = {
        id: ++db.ids.event,
        workspaceId,
        sourceId: source.id,
        conversationId: null,
        channel,
        direction: 'in',
        senderName: `Customer ${day}${i}`,
        senderHandle: channel === 'web' || channel === 'email' ? `customer${day}${i}@example.com` : `@customer${day}${i}`,
        text: ['Do you take reservations for a group of 10?', 'What time do you close today?', 'Is your coconut coffee dairy-free?', 'Is there parking near the shop?'][(day + i) % 4],
        receivedAt,
        correlationId: hex(32),
        providerMessageId: null,
        payload: { note: 'historical sample' },
        deliveries: [],
      }
      const plan: Plan = (day + i) % 7 === 0 ? 'dead' : 'ok'
      for (const r of routesFor[source.id] ?? []) ev.deliveries.push(makeDelivery(db, ev.id, r, plan, receivedAt, now))
      db.events.push(ev)
    }
  }
  db.events.sort((a, b) => (a.receivedAt < b.receivedAt ? 1 : -1))
}

function makeDelivery(db: MockDb, eventId: number, route: MockRoute, plan: 'ok' | 'retrying' | 'dead' | 'pending', receivedAt: string, now: number): MockDelivery {
  const t0 = new Date(receivedAt).getTime() + 1200
  const at = (offsetMs: number) => new Date(t0 + offsetMs).toISOString()
  const d: MockDelivery = { id: ++db.ids.delivery, eventId, destinationId: route.destinationId, routeId: route.id, status: 'Pending', attemptCount: 0, nextAttemptAt: null, lastError: null, attempts: [] }
  switch (plan) {
    case 'pending':
      break
    case 'ok':
      d.attempts.push(attempt(1, at(0), 180 + Math.floor(Math.random() * 200), 204, null))
      d.status = 'Delivered'
      d.attemptCount = 1
      break
    case 'retrying':
      d.attempts.push(attempt(1, at(0), 3012, 503, 'HTTP 503 Service Unavailable'), attempt(2, at(30_000), 2890, 503, 'HTTP 503 Service Unavailable'))
      d.status = 'Retrying'
      d.attemptCount = 2
      d.lastError = 'HTTP 503 Service Unavailable'
      d.nextAttemptAt = new Date(now + 6 * 60_000).toISOString()
      break
    case 'dead': {
      const delays = [0, 30_000, 150_000, 750_000, 2_550_000]
      delays.forEach((ms, i) => d.attempts.push(attempt(i + 1, at(ms), 400 + i * 37, 404, 'HTTP 404 Not Found — webhook deleted')))
      d.status = 'DeadLettered'
      d.attemptCount = 5
      d.lastError = 'HTTP 404 Not Found — webhook deleted'
      break
    }
  }
  return d
}

function payloadFor(channel: Channel, name: string, handle: string, text: string, receivedAt: string): unknown {
  const ts = Math.floor(new Date(receivedAt).getTime() / 1000)
  switch (channel) {
    case 'web':
      return { name, email: handle, message: text }
    case 'instagram':
      return { object: 'instagram', entry: [{ id: '17841405822304914', time: ts, messaging: [{ sender: { id: '1784140582230491401' }, recipient: { id: '17841405822304914' }, timestamp: ts * 1000, message: { mid: 'aWdfZAG' + hex(22), text } }] }] }
    case 'facebook':
      return { object: 'page', entry: [{ id: '108512340096711', time: ts * 1000, messaging: [{ sender: { id: '2409871234567890' }, recipient: { id: '108512340096711' }, timestamp: ts * 1000, message: { mid: 'm_' + hex(24), text } }] }] }
    case 'telegram':
      return { update_id: 90000000 + ts % 100000, message: { message_id: 42, from: { id: 88123456, first_name: name.split(' ')[0], last_name: name.split(' ')[1] ?? '', username: handle.replace('@', '') }, chat: { id: 88123456, type: 'private' }, date: ts, text } }
    case 'email':
      return { to: 'hello@prairiebanhmi.example', from: handle, fromName: name, subject: text.split('\n')[0], text: text.split('\n').slice(2).join('\n'), messageId: `<${hex(16)}@example.com>`, receivedAt }
  }
}
