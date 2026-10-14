import { CHANNEL_META, STATUS_CLASS, STATUS_LABEL } from '../lib/labels'
import type { DeliveryStatus, Direction } from '../lib/types'

/** Square channel glyph (IG / FB / TG / @ / WEB) in the channel colour. */
export function ChannelGlyph({ channel, size = 'md', title, decorative = false }: { channel: string; size?: 'sm' | 'md' | 'lg'; title?: string; decorative?: boolean }) {
  const meta = (CHANNEL_META as Record<string, { label: string; glyph: string }>)[channel]
  const cls = ['glyph', `g-${channel}`, size === 'lg' ? 'glyph-lg' : size === 'sm' ? 'glyph-sm' : ''].filter(Boolean).join(' ')
  const a11y = decorative ? { 'aria-hidden': true as const } : { role: 'img' as const, 'aria-label': meta?.label ?? channel }
  return (
    <span className={cls} title={title ?? meta?.label ?? channel} {...a11y}>
      {meta?.glyph ?? channel.slice(0, 3).toUpperCase()}
    </span>
  )
}

export function StatusPill({ status, title }: { status: DeliveryStatus | string; title?: string }) {
  const cls = (STATUS_CLASS as Record<string, string>)[status] ?? 'pill-mute'
  const label = (STATUS_LABEL as Record<string, string>)[status] ?? status
  return (
    <span className={`pill ${cls}`} title={title}>
      {label}
    </span>
  )
}

export function DirectionTag({ direction }: { direction: Direction }) {
  return (
    <span className={direction === 'out' ? 'dir dir-out' : 'dir'} title={direction === 'out' ? 'Outbound reply' : 'Inbound message'}>
      {direction === 'out' ? 'out' : 'in'}
    </span>
  )
}

export function EnabledPill({ enabled }: { enabled: boolean }) {
  return <span className={`pill ${enabled ? 'pill-ok' : 'pill-mute'}`}>{enabled ? 'Enabled' : 'Disabled'}</span>
}
