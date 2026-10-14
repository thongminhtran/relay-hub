import { useEffect, useState } from 'react'
import { parseDate } from '../lib/format'

export interface Countdown {
  /** Milliseconds left; 0 when closed; null when there is no deadline. */
  remainingMs: number | null
  closed: boolean
  /** Under one hour left. */
  urgent: boolean
}

export const URGENT_THRESHOLD_MS = 60 * 60 * 1000

export function computeCountdown(closesAt: string | null | undefined, nowMs: number): Countdown {
  const d = parseDate(closesAt)
  if (!d) return { remainingMs: null, closed: false, urgent: false }
  const remaining = Math.max(0, d.getTime() - nowMs)
  return { remainingMs: remaining, closed: remaining <= 0, urgent: remaining > 0 && remaining < URGENT_THRESHOLD_MS }
}

/**
 * Ticks once a second (UI only — no network) until the deadline passes, then stops.
 * `closesAt === null` means "no window" → never closed.
 */
export function useCountdown(closesAt: string | null | undefined): Countdown {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const deadline = parseDate(closesAt)?.getTime()
    if (deadline === undefined || deadline <= Date.now()) return
    const id = window.setInterval(() => {
      const t = Date.now()
      setNow(t)
      if (t >= deadline) window.clearInterval(id)
    }, 1000)
    return () => window.clearInterval(id)
  }, [closesAt])

  return computeCountdown(closesAt, now)
}
