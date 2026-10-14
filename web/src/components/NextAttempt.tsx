import { useEffect, useRef } from 'react'
import { useCountdown } from '../hooks/useCountdown'
import { formatFull, formatShortCountdown } from '../lib/format'
import { IconClock } from './Icons'

/** Grace after the scheduled time before refetching: the worker records the attempt 0.2–2 s after `nextAttemptAt`. */
export const NEXT_ATTEMPT_GRACE_MS = 3000

/**
 * Live countdown to a Retrying delivery's `nextAttemptAt`. When it reaches zero it calls `onElapsed` once per deadline
 * (the effect only re-runs when `closed` flips or the deadline changes), a one-shot follow-up to a server-provided
 * schedule, not a timer loop. If the refetch still shows the same deadline, nothing else fires — the user can Refresh.
 */
export function NextAttempt({ nextAttemptAt, onElapsed }: { nextAttemptAt: string; onElapsed: () => void }) {
  const { remainingMs, closed } = useCountdown(nextAttemptAt)
  const callback = useRef(onElapsed)
  useEffect(() => {
    callback.current = onElapsed
  }, [onElapsed])

  useEffect(() => {
    if (!closed) return
    const id = window.setTimeout(() => callback.current(), NEXT_ATTEMPT_GRACE_MS)
    return () => window.clearTimeout(id)
  }, [closed, nextAttemptAt])

  return (
    <span className={`countdown${closed ? ' is-urgent' : ''}`} role="timer" title={`Scheduled ${formatFull(nextAttemptAt)}`}>
      <IconClock />
      {closed ? 'due now' : <span className="num">in {formatShortCountdown(remainingMs ?? 0)}</span>}
    </span>
  )
}
