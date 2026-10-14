/**
 * Shapes from docs/plan-src and the Week 3/4 shared contract. Field names are the API's camelCase names, verbatim.
 */

export type Role = 'Owner' | 'Demo'

export interface User {
  id: number
  email: string
  role: Role
  workspaceId: number
  workspaceName: string
  isDemo: boolean
}

export interface AuthResponse {
  token: string
  expiresAt: string
  user: User
}

/** Channel of an event / conversation. Generic (website form) sources store "web". */
export type Channel = 'web' | 'instagram' | 'facebook' | 'telegram' | 'email'
export const CHANNELS: readonly Channel[] = ['web', 'instagram', 'facebook', 'telegram', 'email']

export type Direction = 'in' | 'out'

export type DeliveryStatus = 'Pending' | 'Delivered' | 'Retrying' | 'DeadLettered'
export const DELIVERY_STATUSES: readonly DeliveryStatus[] = ['Pending', 'Delivered', 'Retrying', 'DeadLettered']
/** Lower-case values accepted by `GET /api/v1/events?status=` ("none" = events without deliveries). */
export type DeliveryStatusFilter = 'pending' | 'delivered' | 'retrying' | 'deadlettered' | 'none'

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
}

export interface EventDeliverySummary {
  id: number
  destinationId: number
  destinationName?: string
  status: DeliveryStatus
  attemptCount: number
  lastError: string | null
}

export interface EventListItem {
  id: number
  channel: Channel
  direction: Direction
  senderName: string | null
  senderHandle: string | null
  text: string
  receivedAt: string
  correlationId: string
  conversationId: number | null
  sourceName?: string
  deliveries: EventDeliverySummary[]
}

export interface DeliveryAttempt {
  attemptNo: number
  startedAt: string
  durationMs: number
  httpStatus: number | null
  error: string | null
}

export interface DeliveryDetail {
  id: number
  eventId?: number
  destinationId: number
  destinationName?: string
  routeId?: number | null
  status: DeliveryStatus
  attemptCount: number
  nextAttemptAt: string | null
  lastError: string | null
  attempts: DeliveryAttempt[]
}

export interface EventDetail {
  id: number
  channel: Channel
  direction: Direction
  conversationId: number | null
  from: { name: string | null; handle: string | null }
  text: string
  receivedAt: string
  correlationId: string
  sourceName?: string
  payload: unknown
  deliveries: DeliveryDetail[]
}

export interface ConversationListItem {
  id: number
  channel: Channel
  sourceId: number
  sourceName: string
  senderName: string | null
  senderHandle: string | null
  lastInboundAt: string | null
  lastOutboundAt: string | null
  lastMessageAt: string
  lastText: string
  lastDirection: Direction
  messageCount: number
}

export interface ReplyWindow {
  canReply: boolean
  windowClosesAt: string | null
  reason: string | null
}

export interface ConversationMessage {
  id: number
  direction: Direction
  text: string
  at: string
  providerMessageId: string | null
  correlationId: string | null
}

export interface ConversationDetail {
  id: number
  channel: Channel
  sourceId: number
  sourceName: string
  senderName: string | null
  senderHandle: string | null
  externalThreadId: string | null
  lastInboundAt: string | null
  lastOutboundAt: string | null
  reply: ReplyWindow
  messages: ConversationMessage[]
}

export interface ReplyResponse {
  messageId: number
  conversationId: number
  channel: Channel
  status: 'Sent' | 'SentDemo'
  providerMessageId: string | null
  at: string
}

export interface StatsSummary {
  today: { events: number; replies: number; delivered: number; retrying: number; deadLettered: number }
  total: { events: number; conversations: number; delivered: number; deadLettered: number }
  successRate: number | null
  byChannel: { channel: Channel; count: number }[]
  series: { date: string; events: number; delivered: number; failed: number }[]
}

export interface NegotiateResponse {
  url: string
  accessToken: string
}

// Realtime hub → client messages
export interface EventCreatedMessage {
  eventId: number
  conversationId: number | null
  channel: Channel
  direction: Direction
  senderName: string | null
  senderHandle: string | null
  text: string
  receivedAt: string
  sourceName: string
}
export interface DeliveryUpdatedMessage {
  deliveryId: number
  eventId: number
  destinationId: number
  status: DeliveryStatus
  attemptCount: number
  lastError: string | null
  nextAttemptAt: string | null
}
export interface ReplySentMessage {
  eventId: number
  conversationId: number
  channel: Channel
  text: string
  at: string
  providerMessageId: string | null
}

// Configuration
export type SourceType = 'generic' | 'instagram' | 'facebook' | 'telegram' | 'email'
export const SOURCE_TYPES: readonly SourceType[] = ['generic', 'instagram', 'facebook', 'telegram', 'email']

export interface Source {
  id: number
  type: SourceType
  name: string
  keyId: string
  externalAccountId: string | null
  config: Record<string, unknown> | null
  isEnabled: boolean
  createdAt: string
}
export interface CreateSourceRequest {
  name: string
  type: SourceType
  externalAccountId?: string
  config?: Record<string, unknown>
}
export interface UpdateSourceRequest {
  name?: string
  isEnabled?: boolean
  externalAccountId?: string
  config?: Record<string, unknown>
}
export interface CreateSourceResponse {
  source: Source
  apiKey: string
  ingestUrl: string
}
export interface RotateKeyResponse {
  source: Source
  apiKey: string
}

export type DestinationType = 'discord' | 'teams' | 'email' | 'telegram' | 'http'
export const DESTINATION_TYPES: readonly DestinationType[] = ['discord', 'teams', 'email', 'telegram', 'http']

export interface Destination {
  id: number
  type: DestinationType
  name: string
  config: Record<string, unknown>
  isEnabled: boolean
}
export interface CreateDestinationRequest {
  name: string
  type: DestinationType
  config: Record<string, unknown>
}
export interface UpdateDestinationRequest {
  name?: string
  config?: Record<string, unknown>
  isEnabled?: boolean
}
export interface DestinationTestResult {
  succeeded: boolean
  httpStatus: number | null
  error: string | null
}

export interface Route {
  id: number
  sourceId: number
  destinationId: number
  template: string
  filter: string | null
  isEnabled: boolean
}
export interface CreateRouteRequest {
  sourceId: number
  destinationId: number
  template?: string
  filter?: string
}
export interface UpdateRouteRequest {
  template?: string
  filter?: string
  isEnabled?: boolean
}

/** RFC 9457 Problem Details as the API emits them. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errors?: Record<string, string[]>
}
