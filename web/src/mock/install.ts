/**
 * Installs a `fetch` interceptor for requests to the API base URL. Anything else (fonts, images) passes through.
 * Enabled only by ./flag.ts — see docs/HANDOFF.md ("Week 3 — dashboard").
 */
import { API_BASE } from '../lib/api'
import { MockApi, type MockRequest } from './handlers'

const DEFAULT_LATENCY_MS: [number, number] = [120, 380]

export function installMockApi(api = new MockApi(), latencyMs: [number, number] = DEFAULT_LATENCY_MS): () => void {
  const realFetch = globalThis.fetch.bind(globalThis)
  const base = new URL(API_BASE)

  globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init)
    const url = new URL(request.url, location.href)
    if (url.origin !== base.origin) return realFetch(request)

    let body: unknown = undefined
    const raw = await request.text()
    if (raw) {
      try {
        body = JSON.parse(raw)
      } catch {
        body = raw
      }
    }
    const req: MockRequest = { method: request.method.toUpperCase(), path: url.pathname, query: url.searchParams, headers: request.headers, body }

    const delay = latencyMs[0] + Math.random() * (latencyMs[1] - latencyMs[0])
    if (delay > 0) await new Promise((r) => setTimeout(r, delay))
    if (request.signal.aborted) throw new DOMException('Aborted', 'AbortError')

    const res = await api.handle(req)
    const headers = new Headers({ 'X-Correlation-Id': crypto.randomUUID?.() ?? String(Date.now()) })
    if (res.body === undefined) return new Response(null, { status: res.status, headers })
    headers.set('Content-Type', res.contentType ?? 'application/json')
    return new Response(JSON.stringify(res.body), { status: res.status, headers })
  }

  console.info(`[relayhub] mock API installed for ${base.origin} — owner login: owner@relayhub.local / relayhub-dev-owner; add ?mock=0 to turn it off`)
  return () => {
    globalThis.fetch = realFetch
  }
}
