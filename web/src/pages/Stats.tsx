import { ChannelGlyph } from '../components/Chips'
import { IconChart } from '../components/Icons'
import { LiveIndicator } from '../components/LiveIndicator'
import { EmptyState, ErrorState, Skeleton } from '../components/Primitives'
import { SeriesChart } from '../components/SeriesChart'
import { useStats } from '../hooks/useData'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { formatPercent } from '../lib/format'
import { channelLabel } from '../lib/labels'
import type { StatsSummary } from '../lib/types'

export function StatsPage() {
  useDocumentTitle('Stats')
  const query = useStats()

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Stats</h1>
          <p className="desc">Today's totals (UTC), the 7-day success rate and volume per channel. Proof that it is a real, running system.</p>
        </div>
        <div className="actions">
          <LiveIndicator />
        </div>
      </header>
      {query.isPending ? (
        <StatsSkeleton />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      ) : query.data ? (
        <StatsView stats={query.data} />
      ) : null}
    </>
  )
}

function StatsSkeleton() {
  return (
    <div className="stack" aria-busy="true" aria-label="Loading stats">
      <div className="tiles">
        {Array.from({ length: 5 }, (_, i) => (
          <div className="tile" key={i}>
            <Skeleton width={70} height={11} />
            <Skeleton width={48} height={28} />
          </div>
        ))}
      </div>
      <div className="stats-grid">
        <div className="card">
          <Skeleton height={220} />
        </div>
        <div className="card">
          <Skeleton height={220} />
        </div>
      </div>
    </div>
  )
}

function StatsView({ stats }: { stats: StatsSummary }) {
  const t = stats.today
  const nothingYet = stats.total.events === 0 && stats.total.conversations === 0
  const maxChannel = Math.max(1, ...stats.byChannel.map((c) => c.count))
  const totalChannel = stats.byChannel.reduce((a, c) => a + c.count, 0)
  const seriesHasData = stats.series.some((p) => p.events || p.delivered || p.failed)

  return (
    <>
      <div className="tiles" role="list" aria-label="Today">
        <Tile label="Events today" value={t.events} sub={`${stats.total.events.toLocaleString()} all time`} />
        <Tile label="Replies today" value={t.replies} sub={`${stats.total.conversations.toLocaleString()} conversations`} />
        <Tile label="Delivered today" value={t.delivered} tone="ok" sub={`${stats.total.delivered.toLocaleString()} all time`} />
        <Tile label="Retrying" value={t.retrying} tone={t.retrying ? 'warn' : undefined} sub="waiting for the next attempt" />
        <Tile label="Dead-lettered today" value={t.deadLettered} tone={t.deadLettered ? 'bad' : undefined} sub={`${stats.total.deadLettered.toLocaleString()} all time`} />
      </div>

      {nothingYet ? (
        <EmptyState title="Nothing to count yet" icon={<IconChart />}>
          Stats fill in as soon as the first message is ingested. Post a webhook to one of your sources or open the demo workspace.
        </EmptyState>
      ) : null}

      <div className="stats-grid">
        <section className="card" aria-label="7-day series">
          {seriesHasData ? (
            <SeriesChart series={stats.series} />
          ) : (
            <EmptyState title="No activity in the last 7 days" inPanel>
              The chart draws events, deliveries and failures per day once there is traffic.
            </EmptyState>
          )}
        </section>

        <div className="stack">
          <section className="card" aria-label="Success rate">
            <span className="h-sm" style={{ marginBottom: 0 }}>
              Delivery success rate · 7 days
            </span>
            <div className="rate">
              <span className={`v ${stats.successRate === null ? 'muted' : stats.successRate >= 0.95 ? 'ok-c' : stats.successRate >= 0.8 ? 'warn-c' : 'bad-c'}`}>
                {formatPercent(stats.successRate)}
              </span>
              <span className="muted" style={{ fontSize: 13 }}>
                delivered ÷ (delivered + dead-lettered)
              </span>
            </div>
            <div className="meter" role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={stats.successRate === null ? undefined : Math.round(stats.successRate * 100)} aria-label="Success rate">
              <div className="fill" style={{ width: `${Math.round((stats.successRate ?? 0) * 100)}%` }} />
            </div>
            {stats.successRate === null ? <p className="policy">No finished deliveries in the last 7 days.</p> : null}
          </section>

          <section className="card" aria-label="By channel">
            <span className="h-sm" style={{ marginBottom: 0 }}>
              Inbound by channel · 7 days
            </span>
            {stats.byChannel.length === 0 ? (
              <p className="policy">No inbound messages in the last 7 days.</p>
            ) : (
              <div className="bars">
                {stats.byChannel.map((c) => (
                  <div className="bar" key={c.channel}>
                    <span className="lb">
                      <ChannelGlyph channel={c.channel} size="sm" decorative />
                      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{channelLabel(c.channel)}</span>
                    </span>
                    <span className="track" aria-hidden="true">
                      <span className="fill" style={{ width: `${(c.count / maxChannel) * 100}%`, display: 'block' }} />
                    </span>
                    <span className="n num" aria-label={`${c.count} messages, ${totalChannel ? Math.round((c.count / totalChannel) * 100) : 0}%`}>
                      {c.count}
                    </span>
                  </div>
                ))}
              </div>
            )}
          </section>
        </div>
      </div>
    </>
  )
}

function Tile({ label, value, sub, tone }: { label: string; value: number; sub?: string; tone?: 'ok' | 'warn' | 'bad' }) {
  return (
    <div className={['tile', tone ?? ''].join(' ').trim()} role="listitem">
      <span className="k">{label}</span>
      <span className="v num">{value.toLocaleString()}</span>
      {sub ? <span className="s">{sub}</span> : null}
    </div>
  )
}
