import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { ChannelGlyph, DirectionTag, StatusPill } from '../components/Chips'
import { IconInbox, IconSearch } from '../components/Icons'
import { LiveIndicator } from '../components/LiveIndicator'
import { EmptyState, ErrorState, Pagination, SkeletonRows, TimeAgo } from '../components/Primitives'
import { useDebounce } from '../hooks/useDebounce'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { useEvents } from '../hooks/useData'
import { CHANNEL_META, STATUS_LABEL } from '../lib/labels'
import { eventFiltersFromParams, eventFiltersToParams, type EventFilters } from '../lib/queries'
import { CHANNELS, type DeliveryStatusFilter, type Direction, type EventListItem } from '../lib/types'

const STATUS_FILTERS: { value: DeliveryStatusFilter; label: string }[] = [
  { value: 'pending', label: STATUS_LABEL.Pending },
  { value: 'retrying', label: STATUS_LABEL.Retrying },
  { value: 'deadlettered', label: STATUS_LABEL.DeadLettered },
  { value: 'delivered', label: STATUS_LABEL.Delivered },
  { value: 'none', label: 'No deliveries' },
]

export function InboxPage() {
  useDocumentTitle('Inbox')
  const [params, setParams] = useSearchParams()
  const urlFilters = useMemo(() => eventFiltersFromParams(params), [params])

  // Filters are URL state. `update` is stable (functional setParams), so effects depending on it do not re-run per keystroke.
  const update = useCallback(
    (patch: Partial<EventFilters>) => {
      setParams(
        (prev) => {
          const next = { ...eventFiltersFromParams(prev), ...patch }
          if (!('page' in patch)) next.page = 1
          return eventFiltersToParams(next)
        },
        { replace: true },
      )
    },
    [setParams],
  )

  // The search box is local state (typed instantly), debounced 300 ms into the server query `q`.
  const [search, setSearch] = useState(urlFilters.q)
  const debouncedSearch = useDebounce(search, 300)
  const lastPushedQ = useRef(urlFilters.q.trim())
  useEffect(() => {
    const q = debouncedSearch.trim()
    if (q === lastPushedQ.current) return
    lastPushedQ.current = q
    update({ q })
  }, [debouncedSearch, update])

  const query = useEvents(urlFilters)
  const data = query.data
  const hasFilters = Boolean(urlFilters.channel || urlFilters.status || urlFilters.direction || urlFilters.q)

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Inbox</h1>
          <p className="desc">Every inbound message and outbound reply, with where each one was delivered.</p>
        </div>
        <div className="actions">
          <LiveIndicator />
        </div>
      </header>

      <div className="toolbar" role="search">
        <div className="search">
          <IconSearch />
          <label className="sr" htmlFor="inbox-q">
            Search messages
          </label>
          <input id="inbox-q" className="input" type="search" placeholder="Search text or sender" autoComplete="off" value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
        <label className="sr" htmlFor="inbox-status">
          Delivery status
        </label>
        <select id="inbox-status" className="select" value={urlFilters.status} onChange={(e) => update({ status: e.target.value as DeliveryStatusFilter | '' })}>
          <option value="">Any status</option>
          {STATUS_FILTERS.map((s) => (
            <option key={s.value} value={s.value}>
              {s.label}
            </option>
          ))}
        </select>
        <label className="sr" htmlFor="inbox-direction">
          Direction
        </label>
        <select id="inbox-direction" className="select" value={urlFilters.direction} onChange={(e) => update({ direction: e.target.value as Direction | '' })}>
          <option value="">In and out</option>
          <option value="in">Inbound</option>
          <option value="out">Outbound</option>
        </select>
      </div>

      <div className="filters" role="toolbar" aria-label="Filter by channel">
        <button type="button" className="chip" aria-pressed={urlFilters.channel === ''} onClick={() => update({ channel: '' })}>
          All channels
        </button>
        {CHANNELS.map((c) => (
          <button key={c} type="button" className="chip" aria-pressed={urlFilters.channel === c} onClick={() => update({ channel: urlFilters.channel === c ? '' : c })}>
            <ChannelGlyph channel={c} size="sm" decorative />
            {CHANNEL_META[c].label}
          </button>
        ))}
      </div>

      <section className="panel" aria-label="Messages" aria-busy={query.isFetching}>
        {query.isPending ? (
          <SkeletonRows rows={8} />
        ) : query.isError ? (
          <div style={{ padding: 16 }}>
            <ErrorState error={query.error} onRetry={() => void query.refetch()} />
          </div>
        ) : data && data.items.length === 0 ? (
          <EmptyState title={hasFilters ? 'No messages match' : 'No messages yet'} inPanel center icon={<IconInbox />}>
            {hasFilters ? 'Clear the search or choose another filter.' : 'Messages appear here as soon as a source posts its first webhook.'}
          </EmptyState>
        ) : data ? (
          <>
            <ul className="list">
              {data.items.map((ev) => (
                <li key={ev.id}>
                  <EventRow event={ev} />
                </li>
              ))}
            </ul>
            <Pagination page={data.page} pageSize={data.pageSize} total={data.total} onPage={(p) => update({ page: p })} />
          </>
        ) : null}
      </section>
    </>
  )
}

function deliverySummary(ev: EventListItem): string {
  const names = ev.deliveries.map((d) => d.destinationName ?? `#${d.destinationId}`)
  return names.length ? `→ ${[...new Set(names)].join(' + ')}` : ''
}

function EventRow({ event: ev }: { event: EventListItem }) {
  const sender = ev.direction === 'out' ? 'You' : ev.senderName || ev.senderHandle || 'Unknown sender'
  return (
    <Link className="row" to={`/app/events/${ev.id}`} aria-label={`${sender}: ${ev.text.slice(0, 80)}`}>
      <ChannelGlyph channel={ev.channel} />
      <span className="nm">
        <span>{sender}</span>
        <DirectionTag direction={ev.direction} />
        {ev.senderHandle && ev.direction === 'in' ? <span className="handle faint">{ev.senderHandle}</span> : null}
      </span>
      <TimeAgo value={ev.receivedAt} className="tm" />
      <span className="tx">{ev.text}</span>
      <span className="st">
        {ev.deliveries.length === 0 ? (
          <span className="pill pill-mute pill-plain">{ev.direction === 'out' ? 'Reply' : 'No route'}</span>
        ) : (
          ev.deliveries.map((d) => (
            <StatusPill key={d.id} status={d.status} title={`${d.destinationName ?? `Destination #${d.destinationId}`} · ${d.attemptCount} attempt${d.attemptCount === 1 ? '' : 's'}${d.lastError ? ` · ${d.lastError}` : ''}`} />
          ))
        )}
        <span className="to">{deliverySummary(ev)}</span>
      </span>
    </Link>
  )
}
