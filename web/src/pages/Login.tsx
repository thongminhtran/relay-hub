import { useState, type FormEvent } from 'react'
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom'
import { Logo } from '../components/Icons'
import { Notice } from '../components/Primitives'
import { ThemeToggle } from '../components/ThemeToggle'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { describeError, isApiError } from '../lib/api'
import { useAuth } from '../lib/auth'

interface LocationState {
  from?: string
  reason?: 'expired'
}

export function LoginPage() {
  useDocumentTitle('Sign in')
  const { isAuthenticated, login, loginDemo } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const state = (location.state ?? {}) as LocationState
  const next = state.from && state.from.startsWith('/app') ? state.from : '/app/inbox'

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState<'owner' | 'demo' | null>(null)
  const [error, setError] = useState<string | null>(null)

  if (isAuthenticated && !busy) return <Navigate to={next} replace />

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    setBusy('owner')
    try {
      await login(email.trim(), password)
      navigate(next, { replace: true })
    } catch (err) {
      if (isApiError(err) && err.status === 401) setError('Invalid email or password.')
      else if (isApiError(err) && err.status === 429) setError('Too many attempts. Wait a minute and try again.')
      else {
        const d = describeError(err)
        setError(d.detail ? `${d.title} — ${d.detail}` : d.title)
      }
    } finally {
      setBusy(null)
    }
  }

  const demo = async () => {
    setError(null)
    setBusy('demo')
    try {
      await loginDemo()
      navigate('/app/inbox', { replace: true })
    } catch (err) {
      const d = describeError(err)
      setError(d.detail ? `${d.title} — ${d.detail}` : d.title)
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="center-page">
      <div className="card-narrow">
        <div className="row-flex" style={{ justifyContent: 'space-between' }}>
          <Link to="/" className="brand">
            <Logo />
            <span>RelayHub</span>
          </Link>
          <ThemeToggle className="btn btn-sm btn-quiet btn-icon" />
        </div>
        <h1>Owner login</h1>
        <p className="muted">Sign in to your workspace. Visitors can skip this and open the read-mostly demo workspace instead.</p>
        {state.reason === 'expired' ? <Notice kind="warn">Your session expired. Sign in again to continue.</Notice> : null}
        <form onSubmit={submit} noValidate>
          <div className="field">
            <label htmlFor="email">Email</label>
            <input id="email" className="input" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} disabled={busy !== null} />
          </div>
          <div className="field">
            <label htmlFor="password">Password</label>
            <input id="password" className="input" type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} disabled={busy !== null} />
          </div>
          {error ? (
            <p className="field-err" role="alert">
              {error}
            </p>
          ) : null}
          <div className="form-actions" style={{ justifyContent: 'space-between' }}>
            <button type="button" className="btn" onClick={demo} disabled={busy !== null}>
              {busy === 'demo' ? <span className="spinner" /> : null}
              Try the demo instead
            </button>
            <button type="submit" className="btn btn-primary" disabled={busy !== null || !email || !password}>
              {busy === 'owner' ? <span className="spinner" /> : null}
              Sign in
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
