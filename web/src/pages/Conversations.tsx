import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { ChannelGlyph } from '../components/Chips'
import { IconChat, IconSearch } from '../components/Icons'
import { LiveIndicator } from '../components/LiveIndicator'
import { EmptyState, ErrorState, Pagination, SkeletonRows, TimeAgo } from '../components/Primitives'
import { useDebounce } from '../hooks/useDebounce'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { useConversations } from '../hooks/useData'
import { CHANNEL_META } from '../lib/labels'
import { conversationFiltersFromParams, conversationFiltersToParams, type ConversationFilters } from '../lib/queries'
import { CHANNELS } from '../lib/types'

export function ConversationsPage() {
  useDocumentTitle('Conversations')
  const [params, setParams] = useSearchParams()
  const filters = useMemo(() => conversationFiltersFromParams(params), [params])

  const update = useCallback(
    (patch: Partial<ConversationFilters>) => {
      setParams(
        (prev) => {
          const next = { ...conversationFiltersFromParams(prev), ...patch }
          if (!('page' in patch)) next.page = 1
          return conversationFiltersToParams(next)
        },
        { replace: true },
      )
    },
    [setParams],
  )

  const [search, setSearch] = useState(filters.q)
  const debounced = useDebounce(search, 300)
  const lastPushedQ = useRef(filters.q.trim())
  useEffect(() => {
    const q = debounced.trim()
    if (q === lastPushedQ.current) return
    lastPushedQ.current = q
    update({ q })
  }, [debounced, update])

  const query = useConversations(filters)
  const data = query.data
  const hasFilters = Boolean(filters.channel || filters.q)

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Conversations</h1>
          <p className="desc">One thread per customer and channel. Open a thread to reply from here.</p>
        </div>
        <div className="actions">
          <LiveIndicator />
        </div>
      </header>

      <div className="toolbar" role="search">
        <div className="search">
          <IconSearch />
          <label className="sr" htmlFor="conv-q">
            Search conversations
          </label>
          <input id="conv-q" className="input" type="search" placeholder="Search name, handle or text" autoComplete="off" value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
      </div>
      <div className="filters" role="toolbar" aria-label="Filter by channel">
        <button type="button" className="chip" aria-pressed={filters.channel === ''} onClick={() => update({ channel: '' })}>
          All channels
        </button>
        {CHANNELS.map((c) => (
          <button key={c} type="button" className="chip" aria-pressed={filters.channel === c} onClick={() => update({ channel: filters.channel === c ? '' : c })}>
            <ChannelGlyph channel={c} size="sm" decorative />
            {CHANNEL_META[c].label}
          </button>
        ))}
      </div>

      <section className="panel" aria-label="Conversations" aria-busy={query.isFetching}>
        {query.isPending ? (
          <SkeletonRows rows={8} />
        ) : query.isError ? (
          <div style={{ padding: 16 }}>
            <ErrorState error={query.error} onRetry={() => void query.refetch()} />
          </div>
        ) : data && data.items.length === 0 ? (
          <EmptyState title={hasFilters ? 'No conversations match' : 'No conversations yet'} inPanel center icon={<IconChat />}>
            {hasFilters ? 'Clear the search or choose another channel.' : 'A conversation starts with the first message from a customer.'}
          </EmptyState>
        ) : data ? (
          <>
            <ul className="list">
              {data.items.map((c) => {
                const who = c.senderName || c.senderHandle || 'Unknown sender'
                return (
                  <li key={c.id}>
                    <Link className="row" to={`/app/conversations/${c.id}`}>
                      <ChannelGlyph channel={c.channel} />
                      <span className="nm">
                        <span>{who}</span>
                        {c.senderHandle && c.senderHandle !== who ? <span className="handle faint">{c.senderHandle}</span> : null}
                      </span>
                      <TimeAgo value={c.lastMessageAt} className="tm" />
                      <span className="tx">
                        {c.lastDirection === 'out' ? <span className="faint">You: </span> : null}
                        {c.lastText}
                      </span>
                      <span className="st">
                        <span className="to">
                          {c.messageCount} message{c.messageCount === 1 ? '' : 's'} · {c.sourceName}
                        </span>
                      </span>
                    </Link>
                  </li>
                )
              })}
            </ul>
            <Pagination page={data.page} pageSize={data.pageSize} total={data.total} onPage={(p) => update({ page: p })} />
          </>
        ) : null}
      </section>
    </>
  )
}
