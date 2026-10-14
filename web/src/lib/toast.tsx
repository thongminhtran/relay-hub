import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react'
import { describeError } from './api'

export type ToastKind = 'info' | 'ok' | 'warn' | 'bad'

export interface Toast {
  id: number
  kind: ToastKind
  title: string
  detail?: string
}

interface ToastApi {
  toasts: Toast[]
  push: (kind: ToastKind, title: string, detail?: string, ttlMs?: number) => number
  error: (e: unknown, fallbackTitle?: string) => number
  dismiss: (id: number) => void
}

const ToastContext = createContext<ToastApi | null>(null)

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(1)

  const dismiss = useCallback((id: number) => setToasts((t) => t.filter((x) => x.id !== id)), [])

  const push = useCallback(
    (kind: ToastKind, title: string, detail?: string, ttlMs = kind === 'bad' ? 8000 : 4500) => {
      const id = nextId.current++
      setToasts((t) => [...t.slice(-4), { id, kind, title, detail }])
      if (ttlMs > 0) window.setTimeout(() => dismiss(id), ttlMs)
      return id
    },
    [dismiss],
  )

  const error = useCallback(
    (e: unknown, fallbackTitle?: string) => {
      const d = describeError(e)
      return push('bad', fallbackTitle && d.title === 'Something went wrong' ? fallbackTitle : d.title, d.detail)
    },
    [push],
  )

  const value = useMemo<ToastApi>(() => ({ toasts, push, error, dismiss }), [toasts, push, error, dismiss])
  return <ToastContext.Provider value={value}>{children}</ToastContext.Provider>
}

export function useToast(): ToastApi {
  const ctx = useContext(ToastContext)
  if (!ctx) throw new Error('useToast must be used inside <ToastProvider>')
  return ctx
}
