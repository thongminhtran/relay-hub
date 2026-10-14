import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { http, isApiError } from './api'
import type { DeliveryUpdatedMessage, EventCreatedMessage, NegotiateResponse, ReplySentMessage } from './types'

export type RealtimeState =
  | 'idle' // never connected (not logged in yet)
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'disconnected' // connection dropped and automatic reconnect gave up
  | 'unavailable' // negotiate answered 503 realtime-unavailable (SignalR not configured) or failed

export interface RealtimeEvents {
  eventCreated: EventCreatedMessage
  deliveryUpdated: DeliveryUpdatedMessage
  replySent: ReplySentMessage
}
export type RealtimeEventName = keyof RealtimeEvents
const EVENT_NAMES: readonly RealtimeEventName[] = ['eventCreated', 'deliveryUpdated', 'replySent']

type Handler<E extends RealtimeEventName> = (message: RealtimeEvents[E]) => void
type AnyHandler = (message: unknown) => void

/** Access tokens from negotiate are short-lived; re-negotiate when the client asks for a token after this long. */
const TOKEN_MAX_AGE_MS = 50 * 60 * 1000

/**
 * Thin wrapper over @microsoft/signalr for the "relayhub" hub (Azure SignalR serverless mode).
 * Flow: POST /api/v1/realtime/negotiate → { url, accessToken } → HubConnection with automatic reconnect.
 * It never fetches anything on a timer; the only requests are negotiate (on connect / token refresh) and the hub transport.
 */
export class RealtimeClient {
  private connection: HubConnection | null = null
  private state: RealtimeState = 'idle'
  private lastError: string | null = null
  private readonly handlers = new Map<RealtimeEventName, Set<AnyHandler>>()
  private readonly stateListeners = new Set<(state: RealtimeState, error: string | null) => void>()
  private negotiated: { url: string; accessToken: string; at: number } | null = null
  private connectPromise: Promise<void> | null = null

  getState(): RealtimeState {
    return this.state
  }
  getLastError(): string | null {
    return this.lastError
  }

  subscribe(listener: (state: RealtimeState, error: string | null) => void): () => void {
    this.stateListeners.add(listener)
    return () => {
      this.stateListeners.delete(listener)
    }
  }

  on<E extends RealtimeEventName>(event: E, handler: Handler<E>): () => void {
    let set = this.handlers.get(event)
    if (!set) {
      set = new Set()
      this.handlers.set(event, set)
    }
    set.add(handler as AnyHandler)
    return () => {
      set.delete(handler as AnyHandler)
    }
  }

  /** Negotiate and start. Safe to call repeatedly; concurrent calls share one attempt. */
  connect(): Promise<void> {
    if (this.connection && this.connection.state === HubConnectionState.Connected) return Promise.resolve()
    if (!this.connectPromise) {
      this.connectPromise = this.doConnect().finally(() => {
        this.connectPromise = null
      })
    }
    return this.connectPromise
  }

  async disconnect(): Promise<void> {
    const c = this.connection
    this.connection = null
    this.negotiated = null
    if (c) {
      try {
        await c.stop()
      } catch {
        /* already stopped */
      }
    }
    this.setState('idle', null)
  }

  private async doConnect(): Promise<void> {
    this.setState('connecting', null)
    if (this.connection) {
      try {
        await this.connection.stop()
      } catch {
        /* ignore */
      }
      this.connection = null
    }

    let negotiated: NegotiateResponse
    try {
      negotiated = await this.negotiate()
    } catch (e) {
      const message = isApiError(e) ? e.title : 'Could not reach the API'
      this.setState('unavailable', message)
      return
    }

    const connection = new HubConnectionBuilder()
      .withUrl(negotiated.url, {
        accessTokenFactory: async () => {
          if (this.negotiated && Date.now() - this.negotiated.at < TOKEN_MAX_AGE_MS) return this.negotiated.accessToken
          const fresh = await this.negotiate()
          return fresh.accessToken
        },
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()

    for (const name of EVENT_NAMES) {
      connection.on(name, (message: unknown) => this.dispatch(name, message))
    }
    connection.onreconnecting(() => this.setState('reconnecting', null))
    connection.onreconnected(() => this.setState('connected', null))
    connection.onclose((err) => {
      if (this.connection !== connection) return // superseded by a newer connection or an explicit disconnect
      this.setState('disconnected', err?.message ?? 'Connection closed')
    })

    this.connection = connection
    try {
      await connection.start()
      if (this.connection === connection) this.setState('connected', null)
    } catch (e) {
      if (this.connection === connection) {
        this.connection = null
        this.setState('disconnected', e instanceof Error ? e.message : 'Could not connect')
      }
    }
  }

  private async negotiate(): Promise<NegotiateResponse> {
    const res = await http.post<NegotiateResponse>('/api/v1/realtime/negotiate')
    this.negotiated = { ...res, at: Date.now() }
    return res
  }

  private dispatch(name: RealtimeEventName, message: unknown): void {
    const set = this.handlers.get(name)
    if (!set) return
    for (const h of set) {
      try {
        h(message)
      } catch (e) {
        console.error(`realtime handler for ${name} failed`, e)
      }
    }
  }

  private setState(state: RealtimeState, error: string | null): void {
    this.state = state
    this.lastError = error
    for (const l of this.stateListeners) l(state, error)
  }
}

/** One client per tab. */
export const realtime = new RealtimeClient()
