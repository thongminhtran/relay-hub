import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TOKEN_STORAGE_KEY } from './api'
import { AuthProvider, useAuth } from './auth'
import { decodeJwt, isExpired, userFromClaims } from './jwt'

const b64url = (s: string) => btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
function makeToken(claims: Record<string, unknown>): string {
  return `${b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }))}.${b64url(JSON.stringify(claims))}.sig`
}
const future = Math.floor(Date.now() / 1000) + 3600
const past = Math.floor(Date.now() / 1000) - 60

describe('jwt helpers', () => {
  it('decodes the payload and derives a user from the contract claims', () => {
    const claims = decodeJwt(makeToken({ sub: '7', email: 'owner@relayhub.local', role: 'Owner', ws: '1', wsn: 'Studio', demo: 'false', exp: future }))
    expect(claims?.role).toBe('Owner')
    expect(isExpired(claims!)).toBe(false)
    expect(userFromClaims(claims!)).toEqual({ id: 7, email: 'owner@relayhub.local', role: 'Owner', workspaceId: 1, workspaceName: 'Studio', isDemo: false })
  })

  it('treats the Demo role as a demo workspace and detects expiry', () => {
    const claims = decodeJwt(makeToken({ sub: '2', role: 'Demo', ws: 2, wsn: 'Prairie Bánh Mì', demo: 'true', exp: past }))!
    expect(isExpired(claims)).toBe(true)
    expect(userFromClaims(claims)?.isDemo).toBe(true)
  })

  it('returns null for garbage', () => {
    expect(decodeJwt('not-a-jwt')).toBeNull()
    expect(decodeJwt('a.b.c')).toBeNull()
  })
})

function Probe() {
  const { isAuthenticated, user, verified, isOwner, canWrite } = useAuth()
  return (
    <div>
      <span data-testid="auth">{String(isAuthenticated)}</span>
      <span data-testid="verified">{String(verified)}</span>
      <span data-testid="owner">{String(isOwner)}</span>
      <span data-testid="write">{String(canWrite)}</span>
      <span data-testid="ws">{user?.workspaceName ?? ''}</span>
    </div>
  )
}

function renderWithAuth(initialPath = '/') {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <AuthProvider>
        <Routes>
          <Route path="*" element={<Probe />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  )
}

describe('AuthProvider hydration', () => {
  const fetchMock = vi.fn<typeof fetch>()
  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('hydrates instantly from the stored token, then confirms with /auth/me', async () => {
    localStorage.setItem(TOKEN_STORAGE_KEY, makeToken({ sub: '1', email: 'owner@relayhub.local', role: 'Owner', ws: '1', wsn: 'Old name', demo: 'false', exp: future }))
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ id: 1, email: 'owner@relayhub.local', role: 'Owner', workspaceId: 1, workspaceName: 'Studio', isDemo: false }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    renderWithAuth()
    // synchronous, before any network answer
    expect(screen.getByTestId('auth').textContent).toBe('true')
    expect(screen.getByTestId('ws').textContent).toBe('Old name')
    expect(screen.getByTestId('verified').textContent).toBe('false')
    expect(screen.getByTestId('write').textContent).toBe('true')

    await waitFor(() => expect(screen.getByTestId('verified').textContent).toBe('true'))
    expect(screen.getByTestId('ws').textContent).toBe('Studio')
    expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/v1\/auth\/me$/)
  })

  it('drops an expired token without calling the API', () => {
    localStorage.setItem(TOKEN_STORAGE_KEY, makeToken({ sub: '2', role: 'Demo', ws: '2', wsn: 'Demo', demo: 'true', exp: past }))
    renderWithAuth()
    expect(screen.getByTestId('auth').textContent).toBe('false')
    expect(localStorage.getItem(TOKEN_STORAGE_KEY)).toBeNull()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('demo users are authenticated but cannot write', async () => {
    localStorage.setItem(TOKEN_STORAGE_KEY, makeToken({ sub: '2', email: 'demo@relayhub.local', role: 'Demo', ws: '2', wsn: 'Prairie Bánh Mì', demo: 'true', exp: future }))
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ id: 2, email: 'demo@relayhub.local', role: 'Demo', workspaceId: 2, workspaceName: 'Prairie Bánh Mì', isDemo: true }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    renderWithAuth()
    expect(screen.getByTestId('owner').textContent).toBe('false')
    expect(screen.getByTestId('write').textContent).toBe('false')
    await waitFor(() => expect(screen.getByTestId('verified').textContent).toBe('true'))
  })

  it('logs out and clears storage when /auth/me answers 401', async () => {
    localStorage.setItem(TOKEN_STORAGE_KEY, makeToken({ sub: '1', role: 'Owner', ws: '1', wsn: 'Studio', demo: 'false', exp: future }))
    fetchMock.mockImplementation(async () => new Response(JSON.stringify({ title: "Unauthorized", status: 401 }), { status: 401, headers: { "Content-Type": "application/problem+json" } }))
    renderWithAuth('/app/inbox')
    await waitFor(() => expect(screen.getByTestId('auth').textContent).toBe('false'))
    expect(localStorage.getItem(TOKEN_STORAGE_KEY)).toBeNull()
  })
})
