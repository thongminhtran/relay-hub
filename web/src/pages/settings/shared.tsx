import type { ReactNode } from 'react'
import { IconLock } from '../../components/Icons'
import { Notice } from '../../components/Primitives'
import { useAuth } from '../../lib/auth'

export const READ_ONLY_DEMO = 'Demo workspace is read-only'
export const OWNER_ONLY = 'Only the workspace owner can change settings'

/** Owner-only gating for configuration writes. `lockReason` is the tooltip for disabled controls. */
export function useWriteGate(): { canWrite: boolean; lockReason: string | undefined } {
  const { canWrite, isDemo } = useAuth()
  return { canWrite, lockReason: canWrite ? undefined : isDemo ? READ_ONLY_DEMO : OWNER_ONLY }
}

/** A button that is disabled with a tooltip when the user may not write. */
export function GatedButton({
  children,
  className = 'btn btn-sm',
  onClick,
  disabled,
  type = 'button',
  ariaLabel,
}: {
  children: ReactNode
  className?: string
  onClick?: () => void
  disabled?: boolean
  type?: 'button' | 'submit'
  ariaLabel?: string
}) {
  const { canWrite, lockReason } = useWriteGate()
  const locked = !canWrite
  return (
    <span title={locked ? lockReason : undefined} style={{ display: 'inline-flex' }}>
      <button type={type} className={className} onClick={onClick} disabled={locked || disabled} aria-disabled={locked || disabled || undefined} aria-label={ariaLabel}>
        {children}
      </button>
    </span>
  )
}

export function ReadOnlyNotice() {
  const { canWrite, lockReason } = useWriteGate()
  if (canWrite) return null
  return (
    <Notice kind="info">
      <span className="row-flex" style={{ gap: 6 }}>
        <IconLock /> {lockReason}. You can browse every setting; create, edit, rotate and delete are disabled.
      </span>
    </Notice>
  )
}

/** Enable/disable switch, gated. */
export function EnabledSwitch({ checked, onChange, busy, label }: { checked: boolean; onChange: (v: boolean) => void; busy?: boolean; label: string }) {
  const { canWrite, lockReason } = useWriteGate()
  const disabled = !canWrite || busy
  return (
    <label className="switch" title={!canWrite ? lockReason : undefined} aria-disabled={disabled || undefined}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} aria-label={label} />
      <span className="track" />
      <span>{checked ? 'Enabled' : 'Disabled'}</span>
    </label>
  )
}
