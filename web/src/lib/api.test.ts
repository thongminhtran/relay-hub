import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { API_BASE, api, ApiError, describeError, NetworkError, qs, setTokenProvider, setUnauthorizedHandler } from './api'

const problemResponse = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json' } })

describe('api()', () => {
  const fetchMock = vi.fn<typeof fetch>()
  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
    setTokenProvider(() => 'tok.en.x')
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    setTokenProvider(() => null)
    setUnauthorizedHandler(null)
    fetchMock.mockReset()
  })

  it('prefixes the base URL, injects the bearer token and parses JSON', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ items: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    const res = await api<{ items: unknown[] }>('/api/v1/events?page=2')
    expect(res).toEqual({ items: [] })
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe(`${API_BASE}/api/v1/events?page=2`)
    expect((init!.headers as Record<string, string>).Authorization).toBe("Bearer tok.en.x")
  })

  it('maps Problem Details to a typed ApiError', async () => {
    fetchMock.mockResolvedValue(
      problemResponse(409, {
        type: 'https://relayhub.dev/problems/reply-window-closed',
        title: 'Reply window closed',
        status: 409,
        detail: "Meta allows replies only within 24 hours of the customer's last message (last message: 2026-10-13T10:00:00Z).",
      }),
    )
    const err = await api('/api/v1/conversations/1/reply', { method: 'POST', body: { text: 'hi' } }).catch((e: unknown) => e)
    expect(err).toBeInstanceOf(ApiError)
    const e = err as ApiError
    expect(e.status).toBe(409)
    expect(e.type).toBe('https://relayhub.dev/problems/reply-window-closed')
    expect(e.code).toBe('reply-window-closed')
    expect(e.title).toBe('Reply window closed')
    expect(e.detail).toMatch(/24 hours/)
    expect(describeError(e)).toEqual({ title: 'Reply window closed', detail: e.detail })
  })

  it('surfaces the first validation message for 400 ValidationProblem bodies', async () => {
    fetchMock.mockResolvedValue(problemResponse(400, { title: 'One or more validation errors occurred.', status: 400, errors: { name: ['Name is required'] } }))
    const e = (await api('/api/v1/sources', { method: 'POST', body: {} }).catch((x: unknown) => x)) as ApiError
    expect(e.status).toBe(400)
    expect(e.firstError).toBe('Name is required')
  })

  it('falls back to the status text when there is no problem body', async () => {
    fetchMock.mockResolvedValue(new Response('<html>gateway</html>', { status: 502, statusText: 'Bad Gateway', headers: { 'Content-Type': 'text/html' } }))
    const e = (await api('/x').catch((x: unknown) => x)) as ApiError
    expect(e.status).toBe(502)
    expect(e.title).toBe('Bad Gateway')
    expect(e.type).toBe('about:blank')
  })

  it('calls the unauthorized handler on 401 for authenticated requests only', async () => {
    const onUnauthorized = vi.fn()
    setUnauthorizedHandler(onUnauthorized)
    fetchMock.mockResolvedValue(problemResponse(401, { title: 'Unauthorized', status: 401 }))

    await expect(api('/api/v1/auth/me')).rejects.toBeInstanceOf(ApiError)
    expect(onUnauthorized).toHaveBeenCalledTimes(1)

    await expect(api('/api/v1/auth/login', { method: 'POST', body: {}, anonymous: true })).rejects.toBeInstanceOf(ApiError)
    expect(onUnauthorized).toHaveBeenCalledTimes(1) // login's own 401 is not a session loss
  })

  it('wraps transport failures in NetworkError', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const e = await api('/health').catch((x: unknown) => x)
    expect(e).toBeInstanceOf(NetworkError)
    expect(describeError(e).title).toBe('Could not reach the API')
  })

  it('returns undefined for 204', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }))
    await expect(api('/api/v1/routes/1', { method: 'DELETE' })).resolves.toBeUndefined()
  })
})

describe('qs()', () => {
  it('skips empty values and encodes the rest', () => {
    expect(qs({ channel: '', status: 'retrying', q: 'bánh mì', page: 1, pageSize: 25, from: undefined, to: null })).toBe('?status=retrying&q=b%C3%A1nh+m%C3%AC&page=1&pageSize=25')
    expect(qs({})).toBe('')
  })
})
