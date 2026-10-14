const fullFmt = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  second: '2-digit',
  timeZoneName: 'short',
})
const timeFmt = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' })
const dayFmt = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' })
const weekdayFmt = new Intl.DateTimeFormat(undefined, { weekday: 'short' })

export function parseDate(value: string | Date | null | undefined): Date | null {
  if (!value) return null
  const d = value instanceof Date ? value : new Date(value)
  return Number.isNaN(d.getTime()) ? null : d
}

/** Full, unambiguous timestamp for tooltips. */
export function formatFull(value: string | Date | null | undefined): string {
  const d = parseDate(value)
  return d ? fullFmt.format(d) : ''
}
export function formatTime(value: string | Date | null | undefined): string {
  const d = parseDate(value)
  return d ? timeFmt.format(d) : ''
}
export function formatDay(value: string | Date | null | undefined): string {
  const d = parseDate(value)
  return d ? dayFmt.format(d) : ''
}
export function formatWeekday(value: string | Date | null | undefined): string {
  const d = parseDate(value)
  return d ? weekdayFmt.format(d) : ''
}

/** "just now", "4 min ago", "3 h ago", "yesterday", "5 days ago", or the date when older than a month. */
export function formatRelative(value: string | Date | null | undefined, nowMs = Date.now()): string {
  const d = parseDate(value)
  if (!d) return ''
  const diff = nowMs - d.getTime()
  const s = Math.round(diff / 1000)
  if (s < -30) return `in ${formatDuration(-diff)}`
  if (s < 45) return 'just now'
  const m = Math.round(s / 60)
  if (m < 60) return `${m} min ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h} h ago`
  const days = Math.floor(h / 24)
  if (days === 1) return 'yesterday'
  if (days < 31) return `${days} days ago`
  return dayFmt.format(d)
}

/** "2 h 05 min", "45 s", "3 min" — coarse durations for next-attempt labels. */
export function formatDuration(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000))
  if (s < 60) return `${s} s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m} min`
  const h = Math.floor(m / 60)
  return `${h} h ${String(m % 60).padStart(2, '0')} min`
}

/** "in 6 min" for a future timestamp, "due now" once it has passed. */
export function formatUntil(value: string | Date | null | undefined, nowMs = Date.now()): string {
  const d = parseDate(value)
  if (!d) return ''
  const diff = d.getTime() - nowMs
  return diff > 0 ? `in ${formatDuration(diff)}` : 'due now'
}

/** hh:mm:ss for the reply-window countdown (hours may exceed 24). */
export function formatCountdown(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
}

export function formatPercent(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—'
  return `${(value * 100).toFixed(digits)}%`
}

export function truncate(text: string, max: number): string {
  if (text.length <= max) return text
  return text.slice(0, max - 1).trimEnd() + '…'
}

/** Initials for avatars: "Linh Nguyen" → "LN", "@handle" → "H". */
export function initials(name: string | null | undefined, fallback = '?'): string {
  const clean = (name ?? '').replace(/^@/, '').trim()
  if (!clean) return fallback
  const parts = clean.split(/\s+/).filter(Boolean)
  const first = parts[0]?.[0] ?? ''
  const last = parts.length > 1 ? (parts[parts.length - 1]?.[0] ?? '') : ''
  return (first + last).toUpperCase() || fallback
}

export function prettyJson(value: unknown): string {
  try {
    return JSON.stringify(value, null, 2) ?? ''
  } catch {
    return String(value)
  }
}
