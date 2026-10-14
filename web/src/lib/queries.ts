import { QueryClient } from '@tanstack/react-query'
import { http, qs } from './api'
import {
  CHANNELS,
  type Channel,
  type ConversationDetail,
  type ConversationListItem,
  type CreateDestinationRequest,
  type CreateRouteRequest,
  type CreateSourceRequest,
  type CreateSourceResponse,
  type DeliveryDetail,
  type DeliveryStatusFilter,
  type DemoFailureMode,
  type DemoResetResponse,
  type DemoSimulateRequest,
  type DemoSimulateResponse,
  type Destination,
  type DestinationTestResult,
  type Direction,
  type EventDetail,
  type EventListItem,
  type Paged,
  type ReplyResponse,
  type RotateKeyResponse,
  type Route,
  type Source,
  type StatsSummary,
  type UpdateDestinationRequest,
  type UpdateRouteRequest,
  type UpdateSourceRequest,
} from './types'

/**
 * One QueryClient for the app. No `refetchInterval` anywhere: the serverless database must be allowed to sleep, so
 * data changes arrive through SignalR (which invalidates these queries) or through the user's own actions.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        refetchInterval: false,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        retry: (failureCount, error) => {
          // Problem Details (4xx/5xx with a body) are final; only network blips get a second try.
          if (error instanceof Error && error.name === 'ApiError') return false
          return failureCount < 1
        },
        staleTime: 30_000,
      },
    },
  })
}

export interface EventFilters {
  channel: Channel | ''
  status: DeliveryStatusFilter | ''
  direction: Direction | ''
  q: string
  page: number
  pageSize: number
}
export const DEFAULT_EVENT_FILTERS: EventFilters = { channel: '', status: '', direction: '', q: '', page: 1, pageSize: 25 }

export function eventsQueryString(f: EventFilters): string {
  return qs({ channel: f.channel, status: f.status, direction: f.direction, q: f.q.trim(), page: f.page, pageSize: f.pageSize })
}

const STATUS_FILTER_VALUES: readonly DeliveryStatusFilter[] = ['pending', 'delivered', 'retrying', 'deadlettered', 'none']

/** Inbox filters live in the URL so a filtered view can be bookmarked. Unknown values fall back to defaults. */
export function eventFiltersFromParams(params: URLSearchParams): EventFilters {
  const page = Number(params.get('page') ?? '1')
  const channel = params.get('channel')
  const status = params.get('status')
  const direction = params.get('direction')
  return {
    ...DEFAULT_EVENT_FILTERS,
    channel: (CHANNELS as readonly string[]).includes(channel ?? '') ? (channel as Channel) : '',
    status: (STATUS_FILTER_VALUES as readonly string[]).includes(status ?? '') ? (status as DeliveryStatusFilter) : '',
    direction: direction === 'in' || direction === 'out' ? direction : '',
    q: params.get('q') ?? '',
    page: Number.isInteger(page) && page > 0 ? page : 1,
  }
}

export function eventFiltersToParams(f: EventFilters): URLSearchParams {
  const sp = new URLSearchParams()
  if (f.channel) sp.set('channel', f.channel)
  if (f.status) sp.set('status', f.status)
  if (f.direction) sp.set('direction', f.direction)
  if (f.q.trim()) sp.set('q', f.q.trim())
  if (f.page > 1) sp.set('page', String(f.page))
  return sp
}

export interface ConversationFilters {
  channel: Channel | ''
  q: string
  page: number
  pageSize: number
}
export const DEFAULT_CONVERSATION_FILTERS: ConversationFilters = { channel: '', q: '', page: 1, pageSize: 25 }

export function conversationFiltersFromParams(params: URLSearchParams): ConversationFilters {
  const page = Number(params.get('page') ?? '1')
  const channel = params.get('channel')
  return {
    ...DEFAULT_CONVERSATION_FILTERS,
    channel: (CHANNELS as readonly string[]).includes(channel ?? '') ? (channel as Channel) : '',
    q: params.get('q') ?? '',
    page: Number.isInteger(page) && page > 0 ? page : 1,
  }
}

export function conversationFiltersToParams(f: ConversationFilters): URLSearchParams {
  const sp = new URLSearchParams()
  if (f.channel) sp.set('channel', f.channel)
  if (f.q.trim()) sp.set('q', f.q.trim())
  if (f.page > 1) sp.set('page', String(f.page))
  return sp
}

export const keys = {
  events: ['events'] as const,
  eventList: (f: EventFilters) => ['events', 'list', f] as const,
  event: (id: number) => ['events', 'detail', id] as const,
  conversations: ['conversations'] as const,
  conversationList: (f: ConversationFilters) => ['conversations', 'list', f] as const,
  conversation: (id: number) => ['conversations', 'detail', id] as const,
  stats: ['stats'] as const,
  sources: ['sources'] as const,
  destinations: ['destinations'] as const,
  routes: ['routes'] as const,
  demoFailureMode: ['demo', 'failure-mode'] as const,
}

export const fetchers = {
  events: (f: EventFilters, signal?: AbortSignal) => http.get<Paged<EventListItem>>(`/api/v1/events${eventsQueryString(f)}`, { signal }),
  event: (id: number, signal?: AbortSignal) => http.get<EventDetail>(`/api/v1/events/${id}`, { signal }),
  conversations: (f: ConversationFilters, signal?: AbortSignal) =>
    http.get<Paged<ConversationListItem>>(`/api/v1/conversations${qs({ channel: f.channel, q: f.q.trim(), page: f.page, pageSize: f.pageSize })}`, { signal }),
  conversation: (id: number, signal?: AbortSignal) => http.get<ConversationDetail>(`/api/v1/conversations/${id}`, { signal }),
  stats: (signal?: AbortSignal) => http.get<StatsSummary>('/api/v1/stats/summary', { signal }),
  sources: (signal?: AbortSignal) => http.get<Source[]>('/api/v1/sources', { signal }),
  destinations: (signal?: AbortSignal) => http.get<Destination[]>('/api/v1/destinations', { signal }),
  routes: (signal?: AbortSignal) => http.get<Route[]>('/api/v1/routes', { signal }),
  demoFailureMode: (signal?: AbortSignal) => http.get<DemoFailureMode>('/api/v1/demo/failure-mode', { signal }),
}

export const mutations = {
  reply: (conversationId: number, text: string) => http.post<ReplyResponse>(`/api/v1/conversations/${conversationId}/reply`, { text }),
  replay: (deliveryId: number) => http.post<DeliveryDetail>(`/api/v1/deliveries/${deliveryId}/replay`),

  createSource: (body: CreateSourceRequest) => http.post<CreateSourceResponse>('/api/v1/sources', body),
  updateSource: (id: number, body: UpdateSourceRequest) => http.put<Source>(`/api/v1/sources/${id}`, body),
  rotateSourceKey: (id: number) => http.post<RotateKeyResponse>(`/api/v1/sources/${id}/rotate-key`),
  deleteSource: (id: number) => http.delete(`/api/v1/sources/${id}`),

  createDestination: (body: CreateDestinationRequest) => http.post<Destination>('/api/v1/destinations', body),
  updateDestination: (id: number, body: UpdateDestinationRequest) => http.put<Destination>(`/api/v1/destinations/${id}`, body),
  testDestination: (id: number) => http.post<DestinationTestResult>(`/api/v1/destinations/${id}/test`),
  deleteDestination: (id: number) => http.delete(`/api/v1/destinations/${id}`),

  createRoute: (body: CreateRouteRequest) => http.post<Route>('/api/v1/routes', body),
  updateRoute: (id: number, body: UpdateRouteRequest) => http.put<Route>(`/api/v1/routes/${id}`, body),
  deleteRoute: (id: number) => http.delete(`/api/v1/routes/${id}`),

  demoSimulate: (body: DemoSimulateRequest) => http.post<DemoSimulateResponse>('/api/v1/demo/simulate', body),
  demoSetFailureMode: (enabled: boolean) => http.put<DemoFailureMode>('/api/v1/demo/failure-mode', { enabled }),
  demoReset: () => http.post<DemoResetResponse>('/api/v1/demo/reset'),
}
