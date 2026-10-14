import { QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { RealtimeProvider } from '../hooks/useRealtime'
import { API_BASE, TOKEN_STORAGE_KEY } from '../lib/api'
import { AuthProvider } from '../lib/auth'
import { createQueryClient, DEFAULT_EVENT_FILTERS, eventFiltersFromParams, eventFiltersToParams } from '../lib/queries'
import { ToastProvider } from '../lib/toast'
import { MockApi } from '../mock/handlers'
import { installMockApi } from '../mock/install'
import { InboxPage } from './Inbox'

describe('event filters ↔ URL', () => {
  it('parses known values and ignores junk', () => {
    const f = eventFiltersFromParams(new URLSearchParams('channel=instagram&status=deadlettered&direction=in&q=vegan&page=3'))
    expect(f).toEqual({ ...DEFAULT_EVENT_FILTERS, channel: 'instagram', status: 'deadlettered', direction: 'in', q: 'vegan', page: 3 })
    const junk = eventFiltersFromParams(new URLSearchParams('channel=whatsapp&status=weird&direction=sideways&page=-2'))
    expect(junk).toEqual(DEFAULT_EVENT_FILTERS)
  })
  it('serialises only non-default values', () => {
    expect(eventFiltersToParams({ ...DEFAULT_EVENT_FILTERS, status: 'retrying', q: '  bánh  ' }).toString()).toBe('status=retrying&q=b%C3%A1nh')
    expect(eventFiltersToParams(DEFAULT_EVENT_FILTERS).toString()).toBe('')
  })
})

describe('<InboxPage> against the mock API', () => {
  let uninstall: () => void
  const requests: string[] = []

  beforeEach(async () => {
    const api = new MockApi()
    uninstall = installMockApi(api, [0, 0])
    const inner = globalThis.fetch
    globalThis.fetch = (input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
      if (url.startsWith(API_BASE)) requests.push(url.slice(API_BASE.length))
      return inner(input, init)
    }
    // log in as the demo user through the mock itself so the token is a valid mock JWT
    const res = await fetch(`${API_BASE}/api/v1/auth/demo`, { method: 'POST' })
    const { token } = (await res.json()) as { token: string }
    localStorage.setItem(TOKEN_STORAGE_KEY, token)
    requests.length = 0
  })
  afterEach(() => uninstall())

  function renderInbox(path = '/app/inbox') {
    const qc = createQueryClient()
    return render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={[path]}>
          <ToastProvider>
            <AuthProvider>
              <RealtimeProvider>
                <Routes>
                  <Route path="/app/inbox" element={<InboxPage />} />
                  <Route path="/app/events/:id" element={<p>event page</p>} />
                </Routes>
              </RealtimeProvider>
            </AuthProvider>
          </ToastProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    )
  }

  it('lists events with channel glyphs and delivery status chips', async () => {
    renderInbox()
    await waitFor(() => expect(screen.getAllByRole('link', { name: /Linh Nguyen/ }).length).toBeGreaterThan(0))
    expect(screen.getAllByText('Delivered').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Retrying').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Dead-lettered').length).toBeGreaterThan(0)
    expect(requests.some((r) => r.startsWith('/api/v1/events?page=1'))).toBe(true)
    // negotiate answered 503 → the manual refresh button is shown instead of a live dot
    await waitFor(() => expect(screen.getByRole('button', { name: /Live updates unavailable/ })).toBeInTheDocument())
  })

  it('filters by channel and status through the server query string', async () => {
    const user = userEvent.setup()
    renderInbox()
    await waitFor(() => expect(screen.getAllByRole('link', { name: /Linh Nguyen/ }).length).toBeGreaterThan(0))

    // the Demo panel has an "Instagram" simulate button too — scope to the channel filter toolbar
    const channelFilters = within(screen.getByRole('toolbar', { name: 'Filter by channel' }))
    await user.click(channelFilters.getByRole('button', { name: 'Instagram' }))
    await waitFor(() => expect(requests.some((r) => r.includes('channel=instagram'))).toBe(true))
    await waitFor(() => expect(screen.queryByRole('link', { name: /Sarah Mitchell/ })).not.toBeInTheDocument())
    expect(channelFilters.getByRole('button', { name: 'Instagram' })).toHaveAttribute('aria-pressed', 'true')

    await user.selectOptions(screen.getByLabelText('Delivery status'), 'deadlettered')
    await waitFor(() => expect(requests.some((r) => r.includes('channel=instagram') && r.includes('status=deadlettered'))).toBe(true))
    await waitFor(() => expect(screen.getByText(/No messages match/)).toBeInTheDocument())
  })

  it('debounces the search box into a single `q` request', async () => {
    const user = userEvent.setup()
    renderInbox()
    await waitFor(() => expect(screen.getAllByRole('link', { name: /Linh Nguyen/ }).length).toBeGreaterThan(0))
    requests.length = 0

    await user.type(screen.getByLabelText('Search messages'), 'vegan')
    // nothing fires per keystroke
    expect(requests.filter((r) => r.includes('q=')).length).toBe(0)
    await waitFor(() => expect(requests.filter((r) => r.includes('q=vegan')).length).toBe(1), { timeout: 2000 })
    expect(requests.filter((r) => r.includes('q=v') && !r.includes('q=vegan')).length).toBe(0)
    await waitFor(() => expect(screen.queryByRole('link', { name: /Sarah Mitchell/ })).not.toBeInTheDocument())
    expect(screen.getAllByRole('link', { name: /Linh Nguyen/ }).length).toBeGreaterThan(0)
  })
})
