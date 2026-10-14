import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useCallback } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ChannelGlyph, DirectionTag, StatusPill } from '../components/Chips'
import { IconBack, IconChat, IconReplay } from '../components/Icons'
import { LiveIndicator } from '../components/LiveIndicator'
import { NextAttempt } from '../components/NextAttempt'
import { CopyButton, EmptyState, ErrorState, JsonView, Skeleton, TimeAgo } from '../components/Primitives'
import { useEvent } from '../hooks/useData'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { isApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { formatFull, formatRelative, formatTime } from '../lib/format'
import { channelLabel } from '../lib/labels'
import { keys, mutations } from '../lib/queries'
import { useToast } from '../lib/toast'
import type { DeliveryDetail, EventDetail } from '../lib/types'

export function EventDetailPage() {
  const params = useParams()
  const id = Number(params.id)
  const query = useEvent(Number.isFinite(id) ? id : null)
  const ev = query.data
  useDocumentTitle(ev ? `Event #${ev.id}` : 'Event')

  return (
    <>
      <Link to="/app/inbox" className="back">
        <IconBack /> Inbox
      </Link>
      {query.isPending ? (
        <section className="panel" aria-busy="true" aria-label="Loading event">
          <div className="d-head">
            <div className="d-who">
              <Skeleton width={40} height={40} />
              <div className="stack-sm">
                <Skeleton width={180} height={18} />
                <Skeleton width={260} />
              </div>
            </div>
          </div>
          <div className="panel-body">
            <Skeleton height={64} />
            <Skeleton height={120} />
          </div>
        </section>
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} title={isApiError(query.error) && query.error.status === 404 ? 'Event not found' : undefined} />
      ) : ev ? (
        <EventView ev={ev} />
      ) : null}
    </>
  )
}

function EventView({ ev }: { ev: EventDetail }) {
  const { isDemo } = useAuth()
  const who = ev.direction === 'out' ? 'You' : ev.from.name || ev.from.handle || 'Unknown sender'
  return (
    <>
      <section className="panel" aria-label="Message">
        <div className="d-head">
          <div className="d-who">
            <ChannelGlyph channel={ev.channel} size="lg" />
            <div>
              <h2>
                {who} <DirectionTag direction={ev.direction} />
              </h2>
              <p>
                {ev.from.handle && ev.from.handle !== who ? `${ev.from.handle} · ` : ''}
                {channelLabel(ev.channel)}
                {ev.sourceName ? ` · ${ev.sourceName}` : ''} · <time dateTime={ev.receivedAt}>{formatFull(ev.receivedAt)}</time> ({formatRelative(ev.receivedAt)})
              </p>
            </div>
          </div>
          <div className="d-meta">
            <LiveIndicator />
            <span className="mono muted">#{ev.id}</span>
            {ev.conversationId ? (
              <Link to={`/app/conversations/${ev.conversationId}`} className="btn btn-sm">
                <IconChat /> Open conversation
              </Link>
            ) : null}
          </div>
        </div>
        <div className="panel-body">
          <p className="msg-text">{ev.text}</p>
          <div className="kv">
            <span>Correlation ID</span>
            <code>{ev.correlationId}</code>
            <CopyButton text={ev.correlationId} />
            <span>Search it in Application Insights to see every log line for this message.</span>
          </div>
          <JsonView value={ev.payload} title="Raw payload" />
        </div>
      </section>

      <section className="stack" aria-label="Delivery timeline">
        <div className="page-head">
          <div>
            <h2 className="h2">Delivery timeline</h2>
            <p className="desc" style={{ fontSize: 13.5 }}>
              One card per route.{' '}
              {isDemo
                ? 'Demo retry profile: 2 s → 4 s → 8 s → 16 s → 32 s, then the delivery is dead-lettered and kept for replay (production waits 30 s → 1 h).'
                : 'Retries back off 30 s → 2 min → 10 min → 30 min → 1 h, then the delivery is dead-lettered and kept for replay.'}
            </p>
          </div>
        </div>
        {ev.deliveries.length === 0 ? (
          <EmptyState title={ev.direction === 'out' ? 'Replies are not routed' : 'No deliveries for this event'}>
            {ev.direction === 'out' ? 'Outbound replies go straight to the channel; nothing to deliver.' : 'No enabled route matched this source when the message arrived.'}
          </EmptyState>
        ) : (
          ev.deliveries.map((d) => <DeliveryCard key={d.id} delivery={d} eventId={ev.id} />)
        )}
      </section>
    </>
  )
}

function DeliveryCard({ delivery: d, eventId }: { delivery: DeliveryDetail; eventId: number }) {
  const toast = useToast()
  const queryClient = useQueryClient()
  // One-shot refetch when the retry schedule elapses (NextAttempt fires it once per nextAttemptAt).
  const refetchEvent = useCallback(() => void queryClient.invalidateQueries({ queryKey: keys.event(eventId) }), [queryClient, eventId])
  const replay = useMutation({
    mutationFn: () => mutations.replay(d.id),
    onSuccess: () => {
      toast.push('ok', 'Replay queued', `Delivery #${d.id} gets one more attempt now.`)
      void queryClient.invalidateQueries({ queryKey: keys.event(eventId) })
      void queryClient.invalidateQueries({ queryKey: keys.events })
      void queryClient.invalidateQueries({ queryKey: keys.stats })
    },
    onError: (err) => {
      // 409: already Delivered / already Pending (someone else replayed first) — refetch so the card shows the truth.
      toast.error(err, 'Replay refused')
      if (isApiError(err) && err.status === 409) void queryClient.invalidateQueries({ queryKey: keys.event(eventId) })
    },
  })

  const canReplay = d.status === 'Retrying' || d.status === 'DeadLettered'
  const name = d.destinationName ?? `Destination #${d.destinationId}`

  return (
    <article className="dl" aria-label={`Delivery to ${name}`}>
      <div className="dl-head">
        <b>{name}</b>
        <span className="tg">delivery #{d.id}</span>
        <span className="sp">
          <StatusPill status={d.status} />
          {canReplay ? (
            <button type="button" className="btn btn-sm btn-primary" onClick={() => replay.mutate()} disabled={replay.isPending}>
              {replay.isPending ? <span className="spinner" /> : <IconReplay />}
              Replay
            </button>
          ) : null}
        </span>
      </div>
      <div className="dl-meta">
        <span>
          Attempts <b className="num">{d.attemptCount}</b>
        </span>
        {d.status === 'Retrying' && d.nextAttemptAt ? (
          <span className="row-flex" style={{ gap: 6 }}>
            Next attempt <NextAttempt key={d.nextAttemptAt} nextAttemptAt={d.nextAttemptAt} onElapsed={refetchEvent} />
          </span>
        ) : null}
        {d.status === 'Pending' ? <span>Queued — waiting for the worker</span> : null}
        {d.status === 'DeadLettered' ? <span>Gave up after {d.attemptCount} attempts — payload and history kept</span> : null}
      </div>
      {d.lastError ? <p className="dl-err">{d.lastError}</p> : null}
      {d.attempts.length > 0 ? (
        <ol className="attempts" aria-label="Attempts">
          <li className="att" aria-hidden="true">
            <span className="att-head">#</span>
            <span className="att-head at">Started</span>
            <span className="att-head">Result</span>
            <span className="att-head">Duration</span>
          </li>
          {d.attempts.map((a) => (
            <li className="att" key={a.attemptNo}>
              <span className="num">#{a.attemptNo}</span>
              <span className="at" title={formatFull(a.startedAt)}>
                {formatTime(a.startedAt)}
              </span>
              <span className={a.error ? 'err' : 'ok-c'}>
                {a.httpStatus !== null ? `HTTP ${a.httpStatus}` : a.error ? 'no response' : 'ok'}
                {a.error ? ` · ${a.error}` : ''}
              </span>
              <span className="num">{a.durationMs} ms</span>
            </li>
          ))}
          {d.status === 'Retrying' && d.nextAttemptAt ? (
            <li className="att next">
              <span className="num">#{d.attemptCount + 1}</span>
              <span className="at">
                <TimeAgo value={d.nextAttemptAt} />
              </span>
              <span>waiting (backoff)</span>
              <span />
            </li>
          ) : null}
          {d.status === 'Pending' ? (
            <li className="att">
              <span className="num">#{d.attemptCount + 1}</span>
              <span className="at">now</span>
              <span>sending…</span>
              <span />
            </li>
          ) : null}
        </ol>
      ) : (
        <p className="policy" style={{ padding: '8px 14px 12px' }}>
          No attempts yet.
        </p>
      )}
    </article>
  )
}
