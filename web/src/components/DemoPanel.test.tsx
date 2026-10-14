import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RATE_LIMIT_MESSAGE, scheduleSimulateFollowUp, SIMULATE_FOLLOW_UP_MS } from '../hooks/useDemo'
import { API_BASE, TOKEN_STORAGE_KEY } from '../lib/api'
import { AuthProvider } from '../lib/auth'
import { createQueryClient, keys } from '../lib/queries'
import { ToastProvider } from '../lib/toast'
import { MockApi } from '../mock/handlers'
import { installMockApi } from '../mock/install'
import { DemoPanel } from './DemoPanel'
import { Toasts } from './Toasts'

describe('<DemoPanel> against the mock API', () => {
  let uninstall: () => void
  const requests: { method: string; path: string; body: unknown }[] = []

  async function loginAs(kind: 'demo' | 'owner') {
    const res =
      kind === 'demo'
        ? await fetch(`${API_BASE}/api/v1/auth/demo`, { method: 'POST' })
        : await fetch(`${API_BASE}/api/v1/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: 'owner@relayhub.local', password: 'relayhub-dev-owner' }) })
    const { token } = (await res.json()) as { token: string }
    localStorage.setItem(TOKEN_STORAGE_KEY, token)
    requests.length = 0
  }

  beforeEach(() => {
    uninstall = installMockApi(new MockApi(), [0, 0])
    const inner = globalThis.fetch
    globalThis.fetch = async (input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
      if (url.startsWith(API_BASE)) {
        let body: unknown = undefined
        if (typeof init?.body === 'string') body = JSON.parse(init.body)
        requests.push({ method: (init?.method ?? 'GET').toUpperCase(), path: url.slice(API_BASE.length), body })
      }
      return inner(input, init)
    }
  })
  afterEach(() => uninstall())

  function renderPanel() {
    const qc = createQueryClient()
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter>
          <ToastProvider>
            <AuthProvider>
              <DemoPanel />
              <Toasts />
            </AuthProvider>
          </ToastProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    )
    return qc
  }

  it('simulates a message with one click and shows the API hint', async () => {
    await loginAs('demo')
    const user = userEvent.setup()
    renderPanel()
    await user.click(screen.getByRole('button', { name: 'Instagram' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/Simulated instagram message from/))
    expect(screen.getByRole('status')).toHaveTextContent('Watch the inbox — the worker stores it in a few seconds.')
    const sim = requests.find((r) => r.path === '/api/v1/demo/simulate')
    expect(sim?.method).toBe('POST')
    expect(sim?.body).toEqual({ channel: 'instagram' })
  })

  it('sends your own text with a sender name and clears the input', async () => {
    await loginAs('demo')
    const user = userEvent.setup()
    renderPanel()
    const form = screen.getByRole('form', { name: 'Send your own message' })
    await user.selectOptions(within(form).getByLabelText('Channel'), 'web')
    await user.type(within(form).getByLabelText('Sender name (optional)'), 'Tim Tran')
    await user.type(within(form).getByLabelText('Message'), 'Do you cater for 30 people?')
    await user.click(within(form).getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(requests.some((r) => r.path === '/api/v1/demo/simulate')).toBe(true))
    expect(requests.find((r) => r.path === '/api/v1/demo/simulate')?.body).toEqual({ channel: 'web', text: 'Do you cater for 30 people?', senderName: 'Tim Tran' })
    await waitFor(() => expect(within(form).getByLabelText('Message')).toHaveValue(''))
  })

  it('reads and flips the failure switch through GET/PUT failure-mode', async () => {
    await loginAs('demo')
    const user = userEvent.setup()
    renderPanel()
    const toggle = screen.getByRole('checkbox', { name: /Simulate failure/ })
    await waitFor(() => expect(requests.some((r) => r.method === 'GET' && r.path === '/api/v1/demo/failure-mode')).toBe(true))
    expect(toggle).not.toBeChecked()
    await user.click(toggle)
    await waitFor(() => expect(toggle).toBeChecked())
    expect(requests.find((r) => r.method === 'PUT')).toMatchObject({ path: '/api/v1/demo/failure-mode', body: { enabled: true } })
    expect(screen.getByRole('status')).toHaveTextContent('Failure mode on')
  })

  it('resets the demo data after confirmation', async () => {
    await loginAs('demo')
    const user = userEvent.setup()
    renderPanel()
    await user.click(screen.getByRole('button', { name: 'Reset' }))
    const dialog = screen.getByRole('dialog', { name: /Reset the demo workspace/ })
    await user.click(within(dialog).getByRole('button', { name: 'Reset demo data' }))
    await waitFor(() => expect(requests.some((r) => r.method === 'POST' && r.path === '/api/v1/demo/reset')).toBe(true))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/Demo data reset/))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('shows the demo-only problem for an owner token', async () => {
    await loginAs('owner')
    const user = userEvent.setup()
    renderPanel()
    await user.click(screen.getByRole('button', { name: 'Telegram' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Demo workspace only'))
  })
})

describe('simulate follow-up', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('invalidates the inbox queries exactly twice (4 s and 10 s), never on a loop', () => {
    const qc = createQueryClient()
    const spy = vi.spyOn(qc, 'invalidateQueries')
    scheduleSimulateFollowUp(qc)
    expect(spy).not.toHaveBeenCalled()
    vi.advanceTimersByTime(SIMULATE_FOLLOW_UP_MS[0])
    expect(spy.mock.calls.map((c) => c[0]?.queryKey)).toEqual([keys.events, keys.conversations, keys.stats])
    vi.advanceTimersByTime(SIMULATE_FOLLOW_UP_MS[1] - SIMULATE_FOLLOW_UP_MS[0])
    expect(spy).toHaveBeenCalledTimes(6)
    vi.advanceTimersByTime(60_000)
    expect(spy).toHaveBeenCalledTimes(6)
  })

  it('has a friendly rate-limit message', () => {
    expect(RATE_LIMIT_MESSAGE).toMatch(/20 messages per minute/)
  })
})
