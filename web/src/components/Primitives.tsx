import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { describeError } from '../lib/api'
import { formatFull, formatRelative } from '../lib/format'
import { IconAlert, IconCheck, IconChevron, IconCopy, IconX } from './Icons'

/** Relative time with the full timestamp as a tooltip. Re-renders only when the parent does (no timers). */
export function TimeAgo({ value, className }: { value: string | null | undefined; className?: string }) {
  if (!value) return <span className={className}>—</span>
  return (
    <time dateTime={value} title={formatFull(value)} className={className ? `num ${className}` : 'num'}>
      {formatRelative(value)}
    </time>
  )
}

export function CopyButton({ text, label = 'Copy', small = true, quiet = true }: { text: string; label?: string; small?: boolean; quiet?: boolean }) {
  const [done, setDone] = useState(false)
  const timer = useRef<number | null>(null)
  useEffect(
    () => () => {
      if (timer.current) window.clearTimeout(timer.current)
    },
    [],
  )
  const copy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(text)
      setDone(true)
      if (timer.current) window.clearTimeout(timer.current)
      timer.current = window.setTimeout(() => setDone(false), 1500)
    } catch {
      // Clipboard blocked (http, permissions): select-and-copy fallback is the user's job; show nothing misleading.
      setDone(false)
    }
  }, [text])
  const cls = ['btn', small ? 'btn-sm' : '', quiet ? 'btn-quiet' : ''].filter(Boolean).join(' ')
  return (
    <button type="button" className={cls} onClick={copy} aria-live="polite">
      {done ? <IconCheck /> : <IconCopy />}
      {done ? 'Copied' : label}
    </button>
  )
}

export function Skeleton({ width, height = 14, circle = false, className }: { width?: number | string; height?: number; circle?: boolean; className?: string }) {
  return <span className={['sk', circle ? 'sk-circle' : '', className ?? ''].join(' ').trim()} style={{ width, height }} aria-hidden="true" />
}

export function SkeletonRows({ rows = 6 }: { rows?: number }) {
  return (
    <div role="status" aria-label="Loading">
      {Array.from({ length: rows }, (_, i) => (
        <div className="sk-row" key={i}>
          <span className="sk" />
          <span className="sk" style={{ width: `${45 + ((i * 17) % 40)}%` }} />
          <span className="sk" />
          <span className="sk" />
        </div>
      ))}
    </div>
  )
}

export function EmptyState({
  title,
  children,
  action,
  icon,
  inPanel = false,
  center = false,
}: {
  title: string
  children?: ReactNode
  action?: ReactNode
  icon?: ReactNode
  inPanel?: boolean
  center?: boolean
}) {
  return (
    <div className={['empty-card', inPanel ? 'in-panel' : '', center ? 'center' : ''].join(' ').trim()}>
      {icon}
      <b>{title}</b>
      {children ? <p>{children}</p> : null}
      {action}
    </div>
  )
}

export function ErrorState({ error, onRetry, title }: { error: unknown; onRetry?: () => void; title?: string }) {
  const d = describeError(error)
  return (
    <div className="error-card" role="alert">
      <div>
        <b>{title ?? d.title}</b>
        {title ? <span>{d.title}</span> : null}
        {d.detail ? <span>{d.detail}</span> : null}
      </div>
      {onRetry ? (
        <button type="button" className="btn btn-sm" onClick={onRetry}>
          Try again
        </button>
      ) : null}
    </div>
  )
}

export function Notice({ kind, children }: { kind?: 'warn' | 'ok' | 'bad' | 'info'; children: ReactNode }) {
  return (
    <div className={['notice', kind && kind !== 'info' ? kind : ''].join(' ').trim()} role={kind === 'bad' ? 'alert' : undefined}>
      {kind === 'warn' || kind === 'bad' ? <IconAlert /> : null}
      <div>{children}</div>
    </div>
  )
}

export function Pagination({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1
  const to = Math.min(total, page * pageSize)
  return (
    <nav className="pager" aria-label="Pagination">
      <span className="num">
        {total === 0 ? 'No results' : `${from}–${to} of ${total}`}
      </span>
      <div className="btns">
        <button type="button" className="btn btn-sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
          Previous
        </button>
        <span className="num" style={{ alignSelf: 'center', padding: '0 4px' }}>
          {page} / {pages}
        </span>
        <button type="button" className="btn btn-sm" disabled={page >= pages} onClick={() => onPage(page + 1)}>
          Next
        </button>
      </div>
    </nav>
  )
}

/** Accessible modal: focus trap-lite (Escape closes, focus moves inside, backdrop click closes). */
export function Modal({ title, children, onClose, narrow = false, description }: { title: string; children: ReactNode; onClose: () => void; narrow?: boolean; description?: string }) {
  const id = useId()
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    const first = ref.current?.querySelector<HTMLElement>('input, select, textarea, button:not([data-close])')
    ;(first ?? ref.current)?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    const { overflow } = document.body.style
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = overflow
      previous?.focus?.()
    }
  }, [onClose])
  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className={narrow ? 'modal narrow' : 'modal'} role="dialog" aria-modal="true" aria-labelledby={`${id}-t`} aria-describedby={description ? `${id}-d` : undefined} ref={ref} tabIndex={-1}>
        <div className="modal-head">
          <div>
            <h2 id={`${id}-t`}>{title}</h2>
            {description ? (
              <p className="desc" id={`${id}-d`}>
                {description}
              </p>
            ) : null}
          </div>
          <button type="button" className="btn btn-quiet btn-icon" onClick={onClose} aria-label="Close" data-close>
            <IconX />
          </button>
        </div>
        {children}
      </div>
    </div>
  )
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = 'Delete',
  danger = true,
  busy = false,
  onConfirm,
  onCancel,
}: {
  title: string
  message: ReactNode
  confirmLabel?: string
  danger?: boolean
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <Modal title={title} onClose={onCancel} narrow>
      <p className="muted">{message}</p>
      <div className="form-actions">
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>
          Cancel
        </button>
        <button type="button" className={danger ? 'btn btn-danger' : 'btn btn-primary'} onClick={onConfirm} disabled={busy}>
          {busy ? <span className="spinner" /> : null}
          {confirmLabel}
        </button>
      </div>
    </Modal>
  )
}

/** Collapsible pretty-printed JSON with syntax tint and a copy button. */
export function JsonView({ value, title = 'Raw payload', defaultOpen = false }: { value: unknown; title?: string; defaultOpen?: boolean }) {
  const text = (() => {
    try {
      return JSON.stringify(value, null, 2) ?? 'null'
    } catch {
      return String(value)
    }
  })()
  return (
    <details className="raw" open={defaultOpen || undefined}>
      <summary>
        <IconChevron /> {title}
      </summary>
      <div className="codeblock">
        <div className="cb-head">
          <span>JSON · {text.length.toLocaleString()} chars</span>
          <CopyButton text={text} label="Copy JSON" />
        </div>
        <pre>
          <HighlightedJson text={text} />
        </pre>
      </div>
    </details>
  )
}

const JSON_TOKEN = /("(?:\\.|[^"\\])*")(\s*:)?|(-?\b\d+(?:\.\d+)?(?:e[+-]?\d+)?\b)|\b(true|false|null)\b/g

function HighlightedJson({ text }: { text: string }) {
  const parts: ReactNode[] = []
  let last = 0
  let i = 0
  for (const m of text.matchAll(JSON_TOKEN)) {
    const idx = m.index ?? 0
    if (idx > last) parts.push(text.slice(last, idx))
    const [whole, str, colon, num, lit] = m
    if (str !== undefined) {
      parts.push(
        <span key={i++} className={colon ? 'j-k' : 'j-s'}>
          {str}
        </span>,
      )
      if (colon) parts.push(colon)
    } else {
      parts.push(
        <span key={i++} className="j-n">
          {num ?? lit}
        </span>,
      )
    }
    last = idx + whole.length
  }
  if (last < text.length) parts.push(text.slice(last))
  return <>{parts}</>
}
