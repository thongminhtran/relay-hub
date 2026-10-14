import type { ProblemDetails } from './types'

/** Base URL of the API, without trailing slash. */
export const API_BASE: string = (import.meta.env.VITE_API_BASE_URL as string | undefined)?.replace(/\/+$/, '') || 'http://localhost:5080'

export const TOKEN_STORAGE_KEY = 'relayhub.token'

/**
 * Typed error for any non-2xx response. `status` is the HTTP status; `type`/`title`/`detail` come from the
 * Problem Details body when the API sent one (it always does), otherwise from the status text.
 */
export class ApiError extends Error {
  readonly status: number
  readonly type: string
  readonly title: string
  readonly detail: string | undefined
  readonly errors: Record<string, string[]> | undefined

  constructor(status: number, problem: ProblemDetails | undefined, fallbackTitle: string) {
    const title = problem?.title?.trim() || fallbackTitle
    super(problem?.detail ? `${title}: ${problem.detail}` : title)
    this.name = 'ApiError'
    this.status = status
    this.type = problem?.type ?? 'about:blank'
    this.title = title
    this.detail = problem?.detail
    this.errors = problem?.errors
  }

  /** Last path segment of the problem `type`, e.g. "reply-window-closed". */
  get code(): string {
    const i = this.type.lastIndexOf('/')
    return i >= 0 ? this.type.slice(i + 1) : this.type
  }

  /** First validation message when the body was a ValidationProblem, else `detail`. */
  get firstError(): string | undefined {
    if (this.errors) {
      for (const list of Object.values(this.errors)) if (list.length) return list[0]
    }
    return this.detail
  }
}

/** Thrown when the request never got a response (offline, DNS, CORS, cold API not listening yet). */
export class NetworkError extends Error {
  constructor(cause: unknown) {
    super('Could not reach the API')
    this.name = 'NetworkError'
    this.cause = cause
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError
}

/** Human-readable title + detail for toasts. */
export function describeError(e: unknown): { title: string; detail?: string } {
  if (e instanceof ApiError) return { title: e.title, detail: e.firstError }
  if (e instanceof NetworkError) return { title: e.message, detail: 'Check your connection or wait for the API to wake up.' }
  if (e instanceof Error) return { title: e.message }
  return { title: 'Something went wrong' }
}

// --- auth plumbing (set by AuthProvider; keeps this module free of React) ---------------------------------------

/** Default provider: the persisted token. AuthProvider swaps in its in-memory copy while mounted. */
export function readStoredToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_STORAGE_KEY)
  } catch {
    return null
  }
}

let tokenProvider: () => string | null = readStoredToken
let unauthorizedHandler: (() => void) | null = null

/** Pass `null` to restore the default (localStorage) provider. */
export function setTokenProvider(fn: (() => string | null) | null): void {
  tokenProvider = fn ?? readStoredToken
}
/** Called once per 401 on an authenticated request so the auth store can log out and redirect to /login. */
export function setUnauthorizedHandler(fn: (() => void) | null): void {
  unauthorizedHandler = fn
}

// --- fetch wrapper ----------------------------------------------------------------------------------------------

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  /** Skip bearer injection and the 401 → logout handling (login, demo, health). */
  anonymous?: boolean
  signal?: AbortSignal
  headers?: Record<string, string>
}

async function readProblem(res: Response): Promise<ProblemDetails | undefined> {
  const ct = res.headers.get('content-type') ?? ''
  if (!ct.includes('json')) return undefined
  try {
    const body: unknown = await res.json()
    return typeof body === 'object' && body !== null ? (body as ProblemDetails) : undefined
  } catch {
    return undefined
  }
}

/**
 * Fetch `${API_BASE}${path}` with JSON in/out, bearer injection and Problem Details → ApiError mapping.
 * 204 / empty bodies resolve to `undefined` (typed by the caller).
 */
export async function api<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...opts.headers }
  if (opts.body !== undefined) headers['Content-Type'] = 'application/json'
  const token = opts.anonymous ? null : tokenProvider()
  if (token) headers.Authorization = `Bearer ${token}`

  let res: Response
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method: opts.method ?? 'GET',
      headers,
      body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
      signal: opts.signal,
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new NetworkError(e)
  }

  if (res.ok) {
    if (res.status === 204 || res.headers.get('content-length') === '0') return undefined as T
    const text = await res.text()
    return (text ? JSON.parse(text) : undefined) as T
  }

  const problem = await readProblem(res)
  const err = new ApiError(res.status, problem, res.statusText || `HTTP ${res.status}`)
  if (res.status === 401 && !opts.anonymous && token) unauthorizedHandler?.()
  throw err
}

export const http = {
  get: <T>(path: string, opts?: Omit<RequestOptions, 'method' | 'body'>) => api<T>(path, { ...opts, method: 'GET' }),
  post: <T>(path: string, body?: unknown, opts?: Omit<RequestOptions, 'method' | 'body'>) => api<T>(path, { ...opts, method: 'POST', body }),
  put: <T>(path: string, body?: unknown, opts?: Omit<RequestOptions, 'method' | 'body'>) => api<T>(path, { ...opts, method: 'PUT', body }),
  delete: <T = void>(path: string, opts?: Omit<RequestOptions, 'method' | 'body'>) => api<T>(path, { ...opts, method: 'DELETE' }),
}

/** Build a query string, skipping empty / undefined values. */
export function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const sp = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue
    sp.set(k, String(v))
  }
  const s = sp.toString()
  return s ? `?${s}` : ''
}
