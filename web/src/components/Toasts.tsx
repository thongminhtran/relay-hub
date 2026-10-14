import { useToast } from '../lib/toast'
import { IconX } from './Icons'

export function Toasts() {
  const { toasts, dismiss } = useToast()
  if (toasts.length === 0) return null
  return (
    <div className="toasts" aria-live="polite" aria-relevant="additions">
      {toasts.map((t) => (
        <div key={t.id} className={`toast ${t.kind}`} role={t.kind === 'bad' ? 'alert' : 'status'}>
          <b>{t.title}</b>
          {t.detail ? <span>{t.detail}</span> : null}
          <button type="button" className="btn btn-quiet btn-icon" onClick={() => dismiss(t.id)} aria-label="Dismiss notification">
            <IconX />
          </button>
        </div>
      ))}
    </div>
  )
}
