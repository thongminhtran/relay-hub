import { useId, useState } from 'react'
import { formatDay, formatWeekday } from '../lib/format'
import type { StatsSummary } from '../lib/types'

type Point = StatsSummary['series'][number]

/** Draw / legend order keeps red and green apart (validated for colour-vision deficiency in both themes). */
const SERIES = [
  { key: 'delivered', label: 'Delivered', cls: 's-delivered' },
  { key: 'events', label: 'Events', cls: 's-events' },
  { key: 'failed', label: 'Failed (dead-lettered)', cls: 's-failed' },
] as const
type SeriesKey = (typeof SERIES)[number]['key']

const W = 640
const H = 240
const PAD = { top: 16, right: 56, bottom: 30, left: 36 }

const TICKS = 4
const NICE_STEPS = [1, 2, 3, 5, 10, 15, 20, 25, 30, 50]

/** Axis maximum = 4 ticks of a "nice" integer step, so every gridline is a whole number. */
function niceMax(v: number): number {
  const raw = Math.max(1, Math.ceil(v / TICKS))
  const pow = 10 ** Math.floor(Math.log10(raw))
  const step = (NICE_STEPS.find((n) => n * pow >= raw) ?? 100) * pow
  return step * TICKS
}

/**
 * Inline SVG line chart for the 7-day series (events / delivered / failed).
 * Accessible: titled, described, a legend, direct end labels, a hover/focus tooltip per day and a table view.
 */
export function SeriesChart({ series }: { series: Point[] }) {
  const id = useId()
  const [active, setActive] = useState<number | null>(null)
  const n = series.length
  if (n === 0) return null

  const maxVal = niceMax(Math.max(1, ...series.flatMap((p) => [p.events, p.delivered, p.failed])))
  const plotW = W - PAD.left - PAD.right
  const plotH = H - PAD.top - PAD.bottom
  const x = (i: number) => PAD.left + (n === 1 ? plotW / 2 : (i / (n - 1)) * plotW)
  const y = (v: number) => PAD.top + plotH - (v / maxVal) * plotH
  const ticks = Array.from({ length: TICKS + 1 }, (_, i) => (i * maxVal) / TICKS)
  const pathFor = (key: SeriesKey) => series.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(p[key]).toFixed(1)}`).join(' ')
  const last = series[n - 1]
  const activePoint = active !== null ? series[active] : null

  // Direct end labels: nudge apart when two series end on the same value.
  const endLabels = SERIES.map((s) => ({ ...s, v: last[s.key], yy: y(last[s.key]) })).sort((a, b) => a.yy - b.yy)
  for (let i = 1; i < endLabels.length; i++) {
    const prev = endLabels[i - 1]
    const cur = endLabels[i]
    if (cur.yy - prev.yy < 12) cur.yy = prev.yy + 12
  }

  return (
    <figure className="stack-sm" aria-labelledby={`${id}-t`} aria-describedby={`${id}-d`} style={{ margin: 0 }}>
      <figcaption className="row-flex" style={{ justifyContent: 'space-between' }}>
        <span id={`${id}-t`} className="h-sm" style={{ marginBottom: 0 }}>
          Last 7 days
        </span>
        <ul className="legend" aria-label="Series">
          {SERIES.map((s) => (
            <li key={s.key}>
              <i className={s.cls} />
              {s.label}
            </li>
          ))}
        </ul>
      </figcaption>
      <p id={`${id}-d`} className="sr">
        Daily counts of inbound events, successful deliveries and dead-lettered deliveries for the last {n} days, oldest first. A table with the
        same numbers follows the chart.
      </p>
      <svg className="chart" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="7-day series chart" onMouseLeave={() => setActive(null)}>
        <g className="grid">
          {ticks.map((t) => (
            <line key={t} x1={PAD.left} x2={W - PAD.right} y1={y(t)} y2={y(t)} />
          ))}
        </g>
        <g className="axis">
          {ticks.map((t) => (
            <text key={t} x={PAD.left - 8} y={y(t) + 4} textAnchor="end">
              {t}
            </text>
          ))}
          {series.map((p, i) => (
            <text key={p.date} x={x(i)} y={H - 10} textAnchor="middle">
              {n <= 7 ? formatWeekday(p.date + 'T12:00:00') : formatDay(p.date + 'T12:00:00')}
            </text>
          ))}
        </g>
        {activePoint !== null && active !== null ? <line x1={x(active)} x2={x(active)} y1={PAD.top} y2={PAD.top + plotH} stroke="var(--line-strong)" strokeDasharray="3 3" /> : null}
        <g className="series">
          {SERIES.map((s) => (
            <g key={s.key} className={s.cls}>
              <path d={pathFor(s.key)} />
              {series.map((p, i) => (
                <circle key={p.date} cx={x(i)} cy={y(p[s.key])} r={active === i ? 5 : 3.5} />
              ))}
            </g>
          ))}
        </g>
        <g className="axis">
          {endLabels.map((s) => (
            <text key={s.key} x={W - PAD.right + 8} y={s.yy + 4} className={s.cls} style={{ fill: 'currentColor', fontWeight: 600 }}>
              {s.v}
            </text>
          ))}
        </g>
        {/* hit targets, one per day (bigger than the marks) */}
        <g>
          {series.map((p, i) => {
            const left = i === 0 ? PAD.left : (x(i - 1) + x(i)) / 2
            const right = i === n - 1 ? W - PAD.right : (x(i) + x(i + 1)) / 2
            return (
              <rect
                key={p.date}
                x={left}
                y={PAD.top}
                width={Math.max(1, right - left)}
                height={plotH}
                fill="transparent"
                tabIndex={0}
                onMouseEnter={() => setActive(i)}
                onFocus={() => setActive(i)}
                onBlur={() => setActive(null)}
                aria-label={`${formatDay(p.date + 'T12:00:00')}: ${p.events} events, ${p.delivered} delivered, ${p.failed} failed`}
              />
            )
          })}
        </g>
        {activePoint && active !== null ? (
          <g transform={`translate(${Math.min(x(active) + 10, W - PAD.right - 130)}, ${PAD.top + 4})`} pointerEvents="none">
            <rect width={130} height={70} rx={6} fill="var(--surface)" stroke="var(--line-strong)" />
            <text x={10} y={18} fontSize={11} fontWeight={600} fill="var(--ink)">
              {formatDay(activePoint.date + 'T12:00:00')}
            </text>
            {SERIES.map((s, i) => (
              <text key={s.key} x={10} y={34 + i * 15} fontSize={11} fill="var(--muted)">
                <tspan className={s.cls} style={{ fill: 'currentColor' }}>
                  ●
                </tspan>{' '}
                {s.label.split(' ')[0]}: <tspan fill="var(--ink)">{activePoint[s.key]}</tspan>
              </text>
            ))}
          </g>
        ) : null}
      </svg>
      <details className="raw">
        <summary style={{ fontSize: 12.5, color: 'var(--muted)', fontWeight: 500 }}>Show as table</summary>
        <div className="table-wrap" style={{ marginTop: 8 }}>
          <table style={{ minWidth: 0 }}>
            <thead>
              <tr>
                <th scope="col">Day</th>
                <th scope="col">Events</th>
                <th scope="col">Delivered</th>
                <th scope="col">Failed</th>
              </tr>
            </thead>
            <tbody>
              {series.map((p) => (
                <tr key={p.date}>
                  <td>{p.date}</td>
                  <td className="num">{p.events}</td>
                  <td className="num">{p.delivered}</td>
                  <td className="num">{p.failed}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </figure>
  )
}
