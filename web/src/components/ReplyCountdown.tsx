import { useCountdown } from '../hooks/useCountdown'
import { formatCountdown, formatFull } from '../lib/format'
import { IconClock } from './Icons'

/**
 * 24-hour reply window: hh:mm:ss until `closesAt`, amber under one hour, red "closed" afterwards.
 * Renders nothing when there is no window (Telegram, email, web).
 */
export function ReplyCountdown({ closesAt }: { closesAt: string | null | undefined }) {
  const { remainingMs, closed, urgent } = useCountdown(closesAt)
  if (remainingMs === null) return null
  const cls = ['countdown', closed ? 'is-closed' : urgent ? 'is-urgent' : ''].join(' ').trim()
  return (
    <span className={cls} title={closed ? `Window closed ${formatFull(closesAt)}` : `Reply window closes ${formatFull(closesAt)}`} role="timer" aria-live={urgent ? 'polite' : 'off'}>
      <IconClock />
      {closed ? 'Window closed' : <span className="num">{formatCountdown(remainingMs)}</span>}
    </span>
  )
}
