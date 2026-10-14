import { API_BASE } from './api'

export type WakePhase = 'api' | 'db' | 'ready'

/** Total budget for the wake-up polling; after this the screen shows a retry button instead of hammering the API. */
export const WAKE_BUDGET_MS = 90_000
const PROBE_TIMEOUT_MS = 15_000
export const WAKE_BACKOFF_MS = [1000, 2000, 4000, 8000, 10000]

async function probe(path: string, signal: AbortSignal): Promise<boolean> {
  const ctrl = new AbortController()
  const onAbort = () => ctrl.abort()
  signal.addEventListener('abort', onAbort)
  const timer = window.setTimeout(() => ctrl.abort(), PROBE_TIMEOUT_MS)
  try {
    const res = await fetch(`${API_BASE}${path}`, { signal: ctrl.signal, cache: 'no-store' })
    return res.ok
  } catch {
    return false
  } finally {
    window.clearTimeout(timer)
    signal.removeEventListener('abort', onAbort)
  }
}

const sleep = (ms: number, signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    const id = window.setTimeout(resolve, ms)
    signal.addEventListener('abort', () => {
      window.clearTimeout(id)
      resolve()
    })
  })

/**
 * Polls /health then /health/ready with exponential backoff until both answer 200 or the budget runs out.
 * This is the ONLY place in the dashboard that polls, and it stops the moment the API is ready.
 */
export async function wake(signal: AbortSignal, onPhase: (p: WakePhase) => void, budgetMs = WAKE_BUDGET_MS): Promise<boolean> {
  const started = Date.now()
  let attempt = 0
  for (const phase of ['api', 'db'] as const) {
    onPhase(phase)
    const path = phase === 'api' ? '/health' : '/health/ready'
    for (;;) {
      if (signal.aborted) return false
      if (await probe(path, signal)) break
      if (Date.now() - started > budgetMs) return false
      await sleep(WAKE_BACKOFF_MS[Math.min(attempt, WAKE_BACKOFF_MS.length - 1)], signal)
      attempt++
    }
  }
  onPhase('ready')
  return true
}
