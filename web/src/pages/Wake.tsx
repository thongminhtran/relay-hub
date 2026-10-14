import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { IconCheck, Logo } from '../components/Icons'
import { ThemeToggle } from '../components/ThemeToggle'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { API_BASE, describeError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { wake, WAKE_BUDGET_MS, type WakePhase } from '../lib/wake'

type StepState = 'todo' | 'active' | 'done' | 'failed'

const PHASES: WakePhase[] = ['api', 'db', 'ready']
const STEPS: { key: WakePhase; title: string; sub: string }[] = [
  { key: 'api', title: 'Waking API', sub: 'App Service · GET /health' },
  { key: 'db', title: 'Waking database', sub: 'Azure SQL serverless · GET /health/ready' },
  { key: 'ready', title: 'Ready', sub: 'Opening your inbox' },
]

interface Run {
  id: number
  startedAt: number
  phase: WakePhase
  failed: boolean
}

export function WakePage() {
  useDocumentTitle('Waking up')
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const wantsDemo = params.get('demo') === '1'
  const { isAuthenticated, loginDemo } = useAuth()

  const [run, setRun] = useState<Run>(() => ({ id: 0, startedAt: Date.now(), phase: 'api', failed: false }))
  const [now, setNow] = useState(() => Date.now())
  const [demoError, setDemoError] = useState<string | null>(null)
  const [demoBusy, setDemoBusy] = useState(false)

  // Read inside the async completion without re-running the wake effect when auth changes mid-run.
  const authRef = useRef(isAuthenticated)
  useEffect(() => {
    authRef.current = isAuthenticated
  }, [isAuthenticated])

  const startDemo = useCallback(async () => {
    setDemoBusy(true)
    setDemoError(null)
    try {
      await loginDemo()
      navigate('/app/inbox', { replace: true })
    } catch (e) {
      const d = describeError(e)
      setDemoError(d.detail ? `${d.title} — ${d.detail}` : d.title)
    } finally {
      setDemoBusy(false)
    }
  }, [loginDemo, navigate])

  useEffect(() => {
    const ctrl = new AbortController()
    const runId = run.id
    const tick = window.setInterval(() => setNow(Date.now()), 500)
    void wake(ctrl.signal, (phase) => setRun((r) => (r.id === runId ? { ...r, phase } : r))).then((ok) => {
      window.clearInterval(tick)
      if (ctrl.signal.aborted) return
      if (!ok) {
        setRun((r) => (r.id === runId ? { ...r, failed: true } : r))
        return
      }
      if (authRef.current) navigate('/app/inbox', { replace: true })
      else if (wantsDemo) void startDemo()
    })
    return () => {
      ctrl.abort()
      window.clearInterval(tick)
    }
  }, [run.id, navigate, wantsDemo, startDemo])

  const retry = () => setRun((r) => ({ id: r.id + 1, startedAt: Date.now(), phase: 'api', failed: false }))

  const current = PHASES.indexOf(run.phase)
  const stepState = (key: WakePhase): StepState => {
    const idx = PHASES.indexOf(key)
    if (idx < current) return 'done'
    if (idx === current) return run.failed ? 'failed' : key === 'ready' ? 'done' : 'active'
    return 'todo'
  }

  const ready = run.phase === 'ready'
  const seconds = Math.max(0, Math.floor((now - run.startedAt) / 1000))

  return (
    <div className="center-page">
      <div className="card-narrow" role="status" aria-live="polite">
        <div className="row-flex" style={{ justifyContent: 'space-between' }}>
          <Link to="/" className="brand">
            <Logo />
            <span>RelayHub</span>
          </Link>
          <ThemeToggle className="btn btn-sm btn-quiet btn-icon" />
        </div>
        <p className="eyebrow">{wantsDemo ? 'Demo workspace' : 'Dashboard'}</p>
        <h1>{run.failed ? 'The API is taking too long' : ready ? 'Ready' : 'Starting RelayHub'}</h1>
        <p className="muted">
          Free-tier Azure services sleep when nobody is using them. A cold start normally takes 20 to 60 seconds; the database resumes on its
          first query.
        </p>
        <ol className="wake-steps">
          {STEPS.map((s) => {
            const st = stepState(s.key)
            return (
              <li key={s.key} className={`wk is-${st}`}>
                <span className="ic">
                  <IconCheck />
                </span>
                <span>
                  <b>{s.title}</b>
                  <small>{s.sub}</small>
                </span>
                <span className="t num">{st === 'done' ? 'ready' : st === 'active' ? `${seconds}s` : st === 'failed' ? 'timed out' : ''}</span>
              </li>
            )
          })}
        </ol>

        {run.failed ? (
          <div className="stack-sm">
            <p className="muted">
              No answer from <code>{API_BASE}</code> after {Math.round(WAKE_BUDGET_MS / 1000)} seconds. The API may be redeploying, or the
              address is wrong.
            </p>
            <div className="row-flex">
              <button type="button" className="btn btn-primary" onClick={retry}>
                Try again
              </button>
              <Link to="/" className="btn btn-quiet">
                Back to the landing page
              </Link>
            </div>
          </div>
        ) : null}

        {ready && !isAuthenticated ? (
          <div className="stack-sm">
            {demoError ? (
              <p className="field-err" role="alert">
                {demoError}
              </p>
            ) : null}
            <div className="row-flex">
              <button type="button" className="btn btn-primary" onClick={() => void startDemo()} disabled={demoBusy}>
                {demoBusy ? <span className="spinner" /> : null}
                {demoBusy ? 'Starting demo session…' : 'Try the live demo'}
              </button>
              <Link to="/login" className="btn">
                Owner login
              </Link>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  )
}
